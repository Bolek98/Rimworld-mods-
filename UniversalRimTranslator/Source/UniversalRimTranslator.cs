// UniversalRimTranslator – zdrojový kód obnovený dekompilací z UniversalRimTranslator.dll (10. 10. 2026), původní zdroják se ztratil.
// Přeložitelné: Source\build.bat  nebo  dotnet build -c Release

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Grammar;

[assembly: AssemblyVersion("0.0.0.0")]
namespace UniversalRimTranslator
{
	public sealed class DeepLBatchResult
	{
		public bool Success;

		public List<string> Translations = new List<string>();

		public string Error;

		public int CharacterCount;

		public int StatusCode;

		public int Attempts;

		public List<string> RawResponses = new List<string>();
	}
	public sealed class DeepLProvider
	{
		private sealed class PlaceholderMap
		{
			public readonly List<string> Original = new List<string>();

			public readonly List<string> Token = new List<string>();
		}

		private readonly HttpClient client = CreateClient();

		private const string FreeEndpoint = "https://api-free.deepl.com/v2/translate";

		private const string ProEndpoint = "https://api.deepl.com/v2/translate";

		private static HttpClient CreateClient()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected O, but got Unknown
			HttpClient val = new HttpClient();
			val.Timeout = TimeSpan.FromSeconds(10.0);
			return val;
		}

		private static string EndpointFor(string apiKey)
		{
			return (apiKey ?? "").Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate";
		}

		public async Task<DeepLBatchResult> TranslateBatchAsync(string apiKey, IList<string> texts, string source, string target, int maxRetries = 3, CancellationToken cancellationToken = default(CancellationToken))
		{
			DeepLBatchResult result = new DeepLBatchResult();
			if (string.IsNullOrWhiteSpace(apiKey))
			{
				result.Error = "DeepL API key is empty.";
				return result;
			}
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Batch is empty.";
				return result;
			}
			List<string> prepared = new List<string>();
			List<PlaceholderMap> placeholders = new List<PlaceholderMap>();
			foreach (string text in texts)
			{
				PlaceholderMap map = new PlaceholderMap();
				prepared.Add(ProtectPlaceholders(text ?? "", map));
				placeholders.Add(map);
			}
			string targetCode = LanguageCodeMapper.ToDeepLTargetCode(target);
			if (string.IsNullOrEmpty(targetCode))
			{
				result.Error = "Unsupported DeepL target language: " + (target ?? "(empty)");
				return result;
			}
			string sourceCode = LanguageCodeMapper.ToDeepLSourceCode(source);
			for (int attempt = 1; attempt <= maxRetries + 1; attempt++)
			{
				result.Attempts = attempt;
				try
				{
					List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>();
					foreach (string text2 in prepared)
					{
						fields.Add(new KeyValuePair<string, string>("text", text2 ?? ""));
					}
					fields.Add(new KeyValuePair<string, string>("target_lang", targetCode));
					if (!string.IsNullOrEmpty(sourceCode))
					{
						fields.Add(new KeyValuePair<string, string>("source_lang", sourceCode));
					}
					HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, EndpointFor(apiKey));
					try
					{
						((HttpHeaders)request.Headers).TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey.Trim());
						request.Content = (HttpContent)new FormUrlEncodedContent((IEnumerable<KeyValuePair<string, string>>)fields);
						HttpResponseMessage response = await ((HttpMessageInvoker)client).SendAsync(request, cancellationToken);
						try
						{
							string body = await response.Content.ReadAsStringAsync();
							result.StatusCode = (int)response.StatusCode;
							if (response.IsSuccessStatusCode)
							{
								List<string> translated = ExtractTranslationTexts(body);
								if (translated.Count != prepared.Count)
								{
									result.Error = "DeepL returned " + translated.Count + " translations for " + prepared.Count + " texts.";
									return result;
								}
								result.Translations.Clear();
								for (int i = 0; i < translated.Count; i++)
								{
									result.Translations.Add(RestorePlaceholders(translated[i], placeholders[i]));
								}
								result.Success = true;
								foreach (string text3 in texts)
								{
									result.CharacterCount += (text3 ?? "").Length;
								}
								return result;
							}
							if (!IsRetryable(response.StatusCode) || attempt > maxRetries)
							{
								result.Error = "HTTP " + (int)response.StatusCode + ": " + body;
								return result;
							}
							await Task.Delay(Backoff(attempt), cancellationToken);
						}
						finally
						{
							((IDisposable)response)?.Dispose();
						}
					}
					finally
					{
						((IDisposable)request)?.Dispose();
					}
				}
				catch (OperationCanceledException)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					if (attempt > maxRetries)
					{
						result.Error = "DeepL: timeout";
						return result;
					}
					await Task.Delay(Backoff(attempt), cancellationToken);
				}
				catch (Exception ex)
				{
					if (attempt > maxRetries)
					{
						result.Error = ex.GetType().Name + ": " + ex.Message;
						return result;
					}
					await Task.Delay(Backoff(attempt), cancellationToken);
				}
			}
			result.Error = "DeepL request failed.";
			return result;
		}

		private static bool IsRetryable(HttpStatusCode code)
		{
			return code == (HttpStatusCode)429 || code == HttpStatusCode.InternalServerError || code == HttpStatusCode.BadGateway || code == HttpStatusCode.ServiceUnavailable || code == HttpStatusCode.GatewayTimeout;
		}

		private static int Backoff(int attempt)
		{
			return Math.Min(10000, 1000 * (1 << Math.Max(0, attempt - 1)));
		}

		private static List<string> ExtractTranslationTexts(string json)
		{
			List<string> list = new List<string>();
			int startIndex = 0;
			bool flag2;
			do
			{
				int num = json.IndexOf("\"text\":\"", startIndex, StringComparison.Ordinal);
				if (num < 0)
				{
					break;
				}
				num += "\"text\":\"".Length;
				StringBuilder stringBuilder = new StringBuilder();
				bool flag = false;
				flag2 = false;
				for (int i = num; i < json.Length; i++)
				{
					char c = json[i];
					if (flag)
					{
						switch (c)
						{
						case '"':
							stringBuilder.Append('"');
							break;
						case '\\':
							stringBuilder.Append('\\');
							break;
						case '/':
							stringBuilder.Append('/');
							break;
						case 'b':
							stringBuilder.Append('\b');
							break;
						case 'f':
							stringBuilder.Append('\f');
							break;
						case 'n':
							stringBuilder.Append('\n');
							break;
						case 'r':
							stringBuilder.Append('\r');
							break;
						case 't':
							stringBuilder.Append('\t');
							break;
						case 'u':
							if (i + 4 < json.Length)
							{
								string s = json.Substring(i + 1, 4);
								if (ushort.TryParse(s, NumberStyles.HexNumber, null, out var result))
								{
									stringBuilder.Append((char)result);
									i += 4;
								}
							}
							break;
						default:
							stringBuilder.Append(c);
							break;
						}
						flag = false;
						continue;
					}
					switch (c)
					{
					case '\\':
						flag = true;
						continue;
					case '"':
						break;
					default:
						stringBuilder.Append(c);
						continue;
					}
					list.Add(stringBuilder.ToString());
					startIndex = i + 1;
					flag2 = true;
					break;
				}
			}
			while (flag2);
			return list;
		}

		private static string Code(string language)
		{
			switch ((language ?? "").Trim().ToLowerInvariant())
			{
			case "english":
			case "en":
				return "EN";
			case "czech":
			case "cs":
				return "CS";
			case "german":
			case "de":
				return "DE";
			case "french":
			case "fr":
				return "FR";
			case "italian":
			case "it":
				return "IT";
			case "japanese":
			case "ja":
				return "JA";
			case "korean":
			case "ko":
				return "KO";
			case "polish":
			case "pl":
				return "PL";
			case "portuguese":
			case "pt":
				return "PT-PT";
			case "portuguesebrazilian":
			case "pt-br":
				return "PT-BR";
			case "russian":
			case "ru":
				return "RU";
			case "spanish":
			case "es":
				return "ES";
			case "dutch":
			case "nl":
				return "NL";
			case "danish":
			case "da":
				return "DA";
			case "swedish":
			case "sv":
				return "SV";
			case "norwegian":
			case "nb":
				return "NB";
			case "ukrainian":
			case "uk":
				return "UK";
			case "chinesesimplified":
			case "zh":
				return "ZH";
			case "chinesetraditional":
			case "zh-hant":
				return "ZH-HANT";
			case "turkish":
			case "tr":
				return "TR";
			case "vietnamese":
			case "vi":
				return "VI";
			default:
				return "";
			}
		}

		private static string ProtectPlaceholders(string text, PlaceholderMap map)
		{
			StringBuilder stringBuilder = new StringBuilder();
			int num = 0;
			while (num < text.Length)
			{
				if (text[num] == '{')
				{
					int num2 = text.IndexOf('}', num + 1);
					if (num2 >= 0)
					{
						string item = text.Substring(num, num2 - num + 1);
						string text2 = "RIMTOKEN" + map.Token.Count + "XYZ";
						map.Original.Add(item);
						map.Token.Add(text2);
						stringBuilder.Append(text2);
						num = num2 + 1;
						continue;
					}
				}
				stringBuilder.Append(text[num]);
				num++;
			}
			return stringBuilder.ToString();
		}

		private static string RestorePlaceholders(string text, PlaceholderMap map)
		{
			string text2 = text ?? "";
			for (int i = 0; i < map.Token.Count; i++)
			{
				text2 = text2.Replace(map.Token[i], map.Original[i]);
			}
			return text2;
		}
	}
	public static class DefTranslationApplier
	{
		private static Dictionary<string, List<Def>> byDefName;

		private static Dictionary<string, List<Def>> byLabel;

		private static readonly Dictionary<string, Type> defTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

		private static bool indexed;

		private static readonly ConcurrentDictionary<string, string> Originals = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		private static bool rulesDirty;

		private static readonly FieldInfo RulesResolvedField = typeof(RulePack).GetField("rulesResolved", BindingFlags.Instance | BindingFlags.NonPublic);

		private static readonly FieldInfo CachedRulesField = typeof(RulePackDef).GetField("cachedRules", BindingFlags.Instance | BindingFlags.NonPublic);

		public static void ApplyAll(TranslationCache cache)
		{
			if (cache == null)
			{
				return;
			}
			if (!UrtMainThread.IsMain)
			{
				// b114: worker thread (scan) -> apply in small slices on the main thread
				List<CachedTranslation> snapshot = cache.GetAll();
				for (int start = 0; start < snapshot.Count; start += 100)
				{
					int from = start;
					UrtMainThread.Post(delegate
					{
						int end = Math.Min(snapshot.Count, from + 100);
						for (int k = from; k < end; k++)
						{
							try
							{
								ApplyOne(snapshot[k]);
							}
							catch
							{
							}
						}
						FlushRuleCaches();
					});
				}
				return;
			}
			EnsureIndex();
			int num = 0;
			foreach (CachedTranslation item in cache.GetAll())
			{
				if (ApplyOne(item))
				{
					num++;
				}
			}
			if (num > 0)
			{
				MLog.Info("[Universal RimTranslator] Applied " + num + " translations to live Defs (index=" + ((byDefName != null) ? byDefName.Count : 0) + ").");
			}
		}

		public static bool ApplyOne(CachedTranslation entry)
		{
			if (entry == null || string.IsNullOrWhiteSpace(entry.Translation))
			{
				return false;
			}
			if (TranslationCache.IsLiveEntry(entry))
			{
				return false;
			}
			if (!UrtMainThread.IsMain)
			{
				// b114: never write into Defs from a worker thread
				UrtMainThread.Post(delegate
				{
					ApplyOne(entry);
					FlushRuleCaches();
				});
				return false;
			}
			if (UrtNonText.IsNonTextKey(entry.Key) || UrtNonText.LooksLikeNonText(entry.SourceText) || UrtNonText.LooksLikeNonText(entry.OriginalSourceText))
			{
				return false;
			}
			string text = entry.Key ?? "";
			string defName = "";
			string text2 = "label";
			int num = text.IndexOf('.');
			if (num > 0 && num < text.Length - 1 && !text.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase))
			{
				defName = text.Substring(0, num);
				text2 = text.Substring(num + 1);
			}
			string text3 = entry.Translation.Trim();
			if (!UrtResultValidator.RulePrefixKept(entry.SourceText, text3))
			{
				return false;
			}
			// 0.9.57: a grammar rule with a changed, lost or invented [token] makes the game write "ERR"
			if (text.IndexOf(".rulesStrings.", StringComparison.OrdinalIgnoreCase) >= 0 && !UrtNameRepair.RuleTokensSame(entry.SourceText, text3))
			{
				return false;
			}
			string[] array = new string[4] { " (plán)", " (ve výstavbě)", " (blueprint)", " (in-progress)" };
			string[] array2 = array;
			foreach (string text4 in array2)
			{
				if (text3.EndsWith(text4, StringComparison.OrdinalIgnoreCase))
				{
					text3 = text3.Substring(0, text3.Length - text4.Length).Trim();
				}
			}
			if (string.IsNullOrEmpty(text3))
			{
				return false;
			}
			EnsureIndex();
			Def def = FindDef(entry.DefType, defName, entry.OriginalSourceText);
			if (def == null)
			{
				def = FindDef(entry.DefType, defName, entry.SourceText);
			}
			if (def == null)
			{
				return false;
			}
			if (ModScanner.IsNameRule(entry.SourceText, def))
			{
				return false;
			}
			string key = OriginalKey(def, text2);
			string text5 = null;
			bool flag = Originals.ContainsKey(key);
			if (!flag)
			{
				text5 = ReadCurrent(def, text2);
			}
			bool flag2 = TrySet(def, text2, text3) && RememberDef(entry, text, text3);
			if (flag2 && !flag)
			{
				Originals.TryAdd(key, text5 ?? entry.OriginalSourceText ?? entry.SourceText ?? "");
			}
			return flag2;
		}

		private static string OriginalKey(Def def, string fieldPath)
		{
			return def.GetType().Name + "|" + def.defName + "." + fieldPath;
		}

		public static bool TryGetOriginal(Type defType, string path, out string original)
		{
			original = null;
			if (defType == null || string.IsNullOrEmpty(path) || Originals.IsEmpty)
			{
				return false;
			}
			return Originals.TryGetValue(defType.Name + "|" + path, out original);
		}

		private static string ReadCurrent(object root, string path)
		{
			try
			{
				string[] array = (path ?? "").Split(new char[1] { '.' });
				object obj = root;
				for (int i = 0; i < array.Length - 1; i++)
				{
					obj = Step(obj, array[i]);
					if (obj == null)
					{
						return null;
					}
				}
				string text = array[array.Length - 1];
				if (obj is IList list && int.TryParse(text, out var result))
				{
					return (result >= 0 && result < list.Count) ? (list[result] as string) : null;
				}
				FieldInfo field = obj.GetType().GetField(text, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				return (field == null) ? null : (field.GetValue(obj) as string);
			}
			catch
			{
				return null;
			}
		}

		private static bool RememberDef(CachedTranslation entry, string key, string text)
		{
			if (entry == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text))
			{
				return true;
			}
			if (!string.Equals(entry.Type, "DefInjected", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (key.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (string.IsNullOrWhiteSpace(entry.DefType))
			{
				return true;
			}
			UrtCzechXml.RememberDef(entry.DefType, key, text);
			return true;
		}

		private static void EnsureIndex()
		{
			if (indexed && byDefName != null)
			{
				return;
			}
			byDefName = new Dictionary<string, List<Def>>(StringComparer.OrdinalIgnoreCase);
			byLabel = new Dictionary<string, List<Def>>(StringComparer.OrdinalIgnoreCase);
			try
			{
				foreach (Type item in GenDefDatabase.AllDefTypesWithDatabases())
				{
					if (item == null)
					{
						continue;
					}
					defTypes[item.Name] = item;
					foreach (Def item2 in GenDefDatabase.GetAllDefsInDatabaseForDef(item))
					{
						Add(item2);
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] Def index build failed: " + ex.GetType().Name + " " + ex.Message);
			}
			indexed = true;
		}

		private static void Add(Def def)
		{
			if (def == null || string.IsNullOrEmpty(def.defName))
			{
				return;
			}
			if (!byDefName.TryGetValue(def.defName, out var value))
			{
				value = new List<Def>(1);
				byDefName[def.defName] = value;
			}
			value.Add(def);
			string label = def.label;
			if (!string.IsNullOrWhiteSpace(label))
			{
				if (!byLabel.TryGetValue(label, out value))
				{
					value = new List<Def>(1);
					byLabel[label] = value;
				}
				value.Add(def);
			}
		}

		private static Type ResolveDefType(string defType)
		{
			if (string.IsNullOrWhiteSpace(defType))
			{
				return null;
			}
			if (defTypes.TryGetValue(defType.Trim(), out var value))
			{
				return value;
			}
			try
			{
				value = GenTypes.GetTypeInAnyAssembly(defType.Trim());
			}
			catch
			{
				value = null;
			}
			if (value != null && !typeof(Def).IsAssignableFrom(value))
			{
				value = null;
			}
			defTypes[defType.Trim()] = value;
			return value;
		}

		private static Def FindDef(string defType, string defName, string sourceText)
		{
			Type type = ResolveDefType(defType);
			bool flag = !string.IsNullOrWhiteSpace(defType) && !string.Equals(defType, "Runtime", StringComparison.OrdinalIgnoreCase) && !string.Equals(defType, "Live", StringComparison.OrdinalIgnoreCase);
			if (!string.IsNullOrEmpty(defName))
			{
				if (type != null)
				{
					Def def = null;
					try
					{
						def = GenDefDatabase.GetDefSilentFail(type, defName, specialCaseForSoundDefs: false);
					}
					catch
					{
						def = null;
					}
					if (def != null)
					{
						return def;
					}
				}
				if (!flag && byDefName != null && byDefName.TryGetValue(defName, out var value) && value.Count == 1)
				{
					return value[0];
				}
				if (flag && type == null && byDefName != null && byDefName.TryGetValue(defName, out value))
				{
					Def def2 = Unique(value, defType);
					if (def2 != null)
					{
						return def2;
					}
				}
				if (flag)
				{
					return null;
				}
			}
			if (!string.IsNullOrWhiteSpace(sourceText) && byLabel != null && byLabel.TryGetValue(sourceText.Trim(), out var value2))
			{
				if (type != null)
				{
					Def def3 = null;
					for (int i = 0; i < value2.Count; i++)
					{
						if (type.IsInstanceOfType(value2[i]))
						{
							if (def3 != null)
							{
								return null;
							}
							def3 = value2[i];
						}
					}
					return def3;
				}
				if (!flag && value2.Count == 1)
				{
					return value2[0];
				}
			}
			return null;
		}

		private static Def Unique(List<Def> defs, string typeName)
		{
			Def def = null;
			for (int i = 0; i < defs.Count; i++)
			{
				if (string.Equals(defs[i].GetType().Name, typeName, StringComparison.OrdinalIgnoreCase))
				{
					if (def != null)
					{
						return null;
					}
					def = defs[i];
				}
			}
			return def;
		}

		private static bool TrySet(Def def, string fieldPath, string text)
		{
			if (def == null || string.IsNullOrEmpty(fieldPath))
			{
				return false;
			}
			try
			{
				if (string.Equals(fieldPath, "label", StringComparison.OrdinalIgnoreCase))
				{
					string targetLanguage = UniversalRimTranslatorSettings.PlayerTargetLanguage();
					if (!string.IsNullOrEmpty(def.label) && LanguageDetector.LooksLikeTargetText(def.label, targetLanguage) && LanguageDetector.HasResidualEnglish(text, targetLanguage))
					{
						return false;
					}
					def.label = text;
					TryClearCache(def);
					return true;
				}
				if (SetPath(def, fieldPath, text))
				{
					TryClearCache(def);
					return true;
				}
			}
			catch
			{
			}
			return false;
		}

		private static bool SetPath(object root, string path, string text)
		{
			string[] array = path.Split(new char[1] { '.' });
			object obj = root;
			object owner = null;
			for (int i = 0; i < array.Length - 1; i++)
			{
				owner = obj;
				obj = Step(obj, array[i]);
				if (obj == null)
				{
					return false;
				}
			}
			string text2 = array[array.Length - 1];
			if (obj is IList list && int.TryParse(text2, out var result))
			{
				if (result < 0 || result >= list.Count || !(list[result] is string))
				{
					return false;
				}
				list[result] = text;
				ForgetResolvedRules(owner);
				return true;
			}
			FieldInfo field = obj.GetType().GetField(text2, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field == null || field.FieldType != typeof(string) || field.IsLiteral || field.IsInitOnly)
			{
				return false;
			}
			// b114: paths, defNames and other technical strings are never overwritten
			if (UrtNonText.IsNonTextField(field.Name) || field.IsDefined(typeof(NoTranslateAttribute), inherit: true) || UrtNonText.LooksLikeNonText(field.GetValue(obj) as string))
			{
				return false;
			}
			field.SetValue(obj, text);
			return true;
		}

		private static object Step(object current, string segment)
		{
			if (current == null || string.IsNullOrEmpty(segment))
			{
				return null;
			}
			if (current is IList list)
			{
				if (int.TryParse(segment, out var result))
				{
					return (result >= 0 && result < list.Count) ? list[result] : null;
				}
				for (int i = 0; i < list.Count; i++)
				{
					object obj = list[i];
					if (obj != null)
					{
						if (obj is Def def && string.Equals(def.defName, segment, StringComparison.OrdinalIgnoreCase))
						{
							return obj;
						}
						FieldInfo field = obj.GetType().GetField("label", BindingFlags.Instance | BindingFlags.Public);
						if (field != null && field.FieldType == typeof(string) && string.Equals((((string)field.GetValue(obj)) ?? "").Replace(' ', '_'), segment, StringComparison.OrdinalIgnoreCase))
						{
							return obj;
						}
					}
				}
				return null;
			}
			FieldInfo field2 = current.GetType().GetField(segment, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			return (field2 == null) ? null : field2.GetValue(current);
		}

		private static void ForgetResolvedRules(object owner)
		{
			if (!(owner is RulePack obj))
			{
				return;
			}
			try
			{
				if (RulesResolvedField != null)
				{
					RulesResolvedField.SetValue(obj, null);
				}
			}
			catch
			{
			}
			rulesDirty = true;
		}

		public static void FlushRuleCaches()
		{
			if (!rulesDirty)
			{
				return;
			}
			if (!UrtMainThread.IsMain)
			{
				UrtMainThread.Post(FlushRuleCaches);
				return;
			}
			rulesDirty = false;
			if (CachedRulesField == null)
			{
				return;
			}
			try
			{
				foreach (RulePackDef item in DefDatabase<RulePackDef>.AllDefsListForReading)
				{
					if (item != null)
					{
						CachedRulesField.SetValue(item, null);
					}
				}
			}
			catch
			{
			}
		}

		public static int ApplyDefsAtStartup(TranslationCache cache, out long ms)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			ms = 0L;
			if (cache == null)
			{
				return 0;
			}
			if (!UrtMainThread.IsMain)
			{
				UrtMainThread.Post(delegate
				{
					ApplyDefsAtStartup(cache, out var _);
				});
				return 0;
			}
			EnsureIndex();
			string b = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			int num = 0;
			foreach (CachedTranslation item in cache.GetAll())
			{
				if (item == null || string.IsNullOrWhiteSpace(item.Translation) || !string.Equals(item.Type, "DefInjected", StringComparison.OrdinalIgnoreCase) || !string.Equals(LanguageCodeMapper.Canonical(item.TargetLanguage), b, StringComparison.OrdinalIgnoreCase) || string.Equals((item.SourceText ?? "").Trim(), item.Translation.Trim(), StringComparison.Ordinal) || !TranslationCache.IsUsableTranslation(item, item.SourceText))
				{
					continue;
				}
				try
				{
					if (ApplyOne(item))
					{
						num++;
					}
				}
				catch
				{
				}
			}
			FlushRuleCaches();
			ms = stopwatch.ElapsedMilliseconds;
			return num;
		}

		public static int ApplyRulesAtStartup(TranslationCache cache)
		{
			if (cache == null)
			{
				return 0;
			}
			if (!UrtMainThread.IsMain)
			{
				UrtMainThread.Post(delegate
				{
					ApplyRulesAtStartup(cache);
				});
				return 0;
			}
			string b = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			int num = 0;
			foreach (CachedTranslation item in cache.GetAll())
			{
				if (item == null || item.Key == null || item.Key.IndexOf(".rulesStrings.", StringComparison.OrdinalIgnoreCase) < 0 || !string.Equals(LanguageCodeMapper.Canonical(item.TargetLanguage), b, StringComparison.OrdinalIgnoreCase) || !TranslationCache.IsUsableTranslation(item, item.SourceText))
				{
					continue;
				}
				try
				{
					if (ApplyOne(item))
					{
						num++;
					}
				}
				catch
				{
				}
			}
			FlushRuleCaches();
			return num;
		}

		private static void TryClearCache(Def def)
		{
			try
			{
				def?.ClearCachedData();
			}
			catch
			{
			}
		}
	}
	internal static class ExtraHttp
	{
		public static readonly HttpClient Client;

		public static readonly HttpClient BrowserClient;

		static ExtraHttp()
		{
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_0011: Expected O, but got Unknown
			//IL_0044: Unknown result type (might be due to invalid IL or missing references)
			//IL_004e: Expected O, but got Unknown
			UrtNet.Configure();
			BrowserClient = new HttpClient();
			BrowserClient.Timeout = TimeSpan.FromSeconds(30.0);
			((HttpHeaders)BrowserClient.DefaultRequestHeaders).TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0");
			Client = new HttpClient();
			Client.Timeout = TimeSpan.FromSeconds(60.0);
			Client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalRimTranslator/" + UrtVersion.Number);
		}
	}
	public sealed class LingvaTranslateProvider : IUniversalTranslationProvider
	{
		public string Name => "Lingva";

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			string root = ((settings != null && !string.IsNullOrWhiteSpace(settings.LingvaEndpoint)) ? settings.LingvaEndpoint.Trim().TrimEnd(new char[1] { '/' }) : "https://lingva.lunar.icu");
			string src = Code(source);
			string dst = Code(target);
			string[] slots = new string[texts.Count];
			int status = 0;
			string error = "";
			await UrtOptEngine.ForEachAsync(texts.Count, 3, async delegate(int i)
			{
				string text = texts[i] ?? "";
				if (UrtOptEngine.IsCoolingDown(Name))
				{
					slots[i] = "";
					return;
				}
				try
				{
					string url = root + "/api/v1/" + src + "/" + dst + "/" + Uri.EscapeDataString(text);
					HttpResponseMessage resp = await ExtraHttp.Client.GetAsync(url, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					try
					{
						string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
						string translated = (resp.IsSuccessStatusCode ? ProviderJsonHelper.ExtractFirstString(json, "translation") : null);
						slots[i] = (string.IsNullOrWhiteSpace(translated) ? "" : translated);
						if (slots[i].Length == 0)
						{
							status = (int)resp.StatusCode;
							if (status == 429)
							{
								UrtOptEngine.CoolDown(Name, 60);
							}
							string apiError = ProviderJsonHelper.ExtractFirstString(json, "error");
							error = "Lingva: " + ((!string.IsNullOrWhiteSpace(apiError)) ? apiError : ((!resp.IsSuccessStatusCode) ? ("HTTP " + status) : "no translation field"));
						}
					}
					finally
					{
						((IDisposable)resp)?.Dispose();
					}
				}
				catch (OperationCanceledException)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					slots[i] = "";
					error = "Lingva: request timed out";
				}
				catch (Exception ex)
				{
					slots[i] = "";
					error = "Lingva: " + ex.GetType().Name;
				}
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			for (int j = 0; j < texts.Count; j++)
			{
				result.CharacterCount += (texts[j] ?? "").Length;
				result.Translations.Add(slots[j] ?? "");
			}
			result.StatusCode = status;
			if (error.Length > 0)
			{
				result.Error = error;
			}
			result.Success = result.Translations.Exists((string t) => !string.IsNullOrWhiteSpace(t));
			return result;
		}

		private static string Code(string language)
		{
			string language2 = LanguageCodeMapper.Canonical(language);
			if (LanguageCodeMapper.IsAuto(language2))
			{
				return "auto";
			}
			string text = LanguageCodeMapper.ToGoogleCode(language2).ToLowerInvariant();
			return text switch
			{
				"zh-tw" => "zh_HANT", 
				"zh-cn" => "zh", 
				"pt-pt" => "pt", 
				_ => string.IsNullOrEmpty(text) ? "auto" : text, 
			};
		}
	}
	public sealed class GoogleWebTranslateProvider : IUniversalTranslationProvider
	{
		public string Name => "Google web";

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			string dst = LanguageCodeMapper.ToGoogleCode(LanguageCodeMapper.Canonical(target));
			string[] slots = new string[texts.Count];
			int[] statusCodes = new int[texts.Count];
			string[] errorSlots = new string[texts.Count];
			await UrtOptEngine.ForEachAsync(texts.Count, 6, async delegate(int i)
			{
				string text = texts[i] ?? "";
				if (UrtOptEngine.IsCoolingDown(Name))
				{
					slots[i] = "";
					errorSlots[i] = "cooldown after HTTP 429";
				}
				else
				{
					string encoded = Uri.EscapeDataString(text);
					if (encoded.Length <= 7000)
					{
						try
						{
							string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(dst) + "&dt=t&q=" + encoded;
							HttpResponseMessage resp = await ExtraHttp.Client.GetAsync(url, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							try
							{
								string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
								statusCodes[i] = (int)resp.StatusCode;
								if (!resp.IsSuccessStatusCode)
								{
									errorSlots[i] = "HTTP " + (int)resp.StatusCode;
									slots[i] = "";
									if (resp.StatusCode == (HttpStatusCode)429)
									{
										UrtOptEngine.CoolDown(Name, 60);
									}
									return;
								}
								if (string.IsNullOrEmpty(json) || json[0] != '[')
								{
									slots[i] = "";
									errorSlots[i] = "response was not a translation array";
									return;
								}
								string translatedText = ExtractGoogleTranslations(json);
								if (string.IsNullOrWhiteSpace(translatedText))
								{
									slots[i] = "";
									errorSlots[i] = "response contained no translation segments";
									return;
								}
								slots[i] = translatedText;
							}
							finally
							{
								((IDisposable)resp)?.Dispose();
							}
							return;
						}
						catch (OperationCanceledException)
						{
							if (cancellationToken.IsCancellationRequested)
							{
								throw;
							}
							slots[i] = "";
							errorSlots[i] = "request timed out";
							return;
						}
						catch (Exception ex)
						{
							slots[i] = "";
							errorSlots[i] = ex.GetType().Name;
							return;
						}
					}
					slots[i] = "";
					errorSlots[i] = "text too long for Google web GET request";
				}
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			for (int k = 0; k < texts.Count; k++)
			{
				result.CharacterCount += (texts[k] ?? "").Length;
				result.Translations.Add(slots[k] ?? "");
			}
			result.Success = result.Translations.Exists((string t) => !string.IsNullOrWhiteSpace(t));
			int errorStatus = 0;
			int firstStatus = 0;
			string detail = "";
			for (int j = 0; j < statusCodes.Length; j++)
			{
				int code = statusCodes[j];
				if (firstStatus == 0 && code != 0)
				{
					firstStatus = code;
				}
				if (errorStatus == 0 && code != 0 && (code < 200 || code >= 300))
				{
					errorStatus = code;
				}
				if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(errorSlots[j]))
				{
					detail = errorSlots[j];
				}
			}
			result.StatusCode = ((errorStatus != 0) ? errorStatus : ((!result.Success) ? firstStatus : 0));
			if (string.IsNullOrWhiteSpace(detail) && !result.Success)
			{
				detail = "no usable translations";
			}
			if (!string.IsNullOrWhiteSpace(detail))
			{
				result.Error = "Google web: " + detail;
			}
			else if (!result.Success)
			{
				result.Error = "Google web: no usable translations";
			}
			return result;
		}

		private static string ExtractGoogleTranslations(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
			{
				return "";
			}
			int index = 0;
			SkipJsonWhitespace(json, ref index);
			if (index >= json.Length || json[index++] != '[')
			{
				return "";
			}
			SkipJsonWhitespace(json, ref index);
			if (index >= json.Length || json[index++] != '[')
			{
				return "";
			}
			StringBuilder stringBuilder = new StringBuilder();
			while (index < json.Length)
			{
				SkipJsonWhitespace(json, ref index);
				if (index >= json.Length || json[index] != '[')
				{
					break;
				}
				int num = FindJsonArrayEnd(json, index);
				if (num < 0)
				{
					break;
				}
				int index2 = index + 1;
				SkipJsonWhitespace(json, ref index2);
				if (TryReadJsonString(json, ref index2, out var value))
				{
					stringBuilder.Append(value);
				}
				index = num + 1;
				SkipJsonWhitespace(json, ref index);
				if (index < json.Length && json[index] == ',')
				{
					index++;
					continue;
				}
				break;
			}
			return stringBuilder.ToString();
		}

		private static int FindJsonArrayEnd(string json, int start)
		{
			int num = 0;
			bool flag = false;
			bool flag2 = false;
			for (int i = start; i < json.Length; i++)
			{
				char c = json[i];
				if (flag)
				{
					if (flag2)
					{
						flag2 = false;
						continue;
					}
					switch (c)
					{
					case '\\':
						flag2 = true;
						break;
					case '"':
						flag = false;
						break;
					}
					continue;
				}
				switch (c)
				{
				case '"':
					flag = true;
					break;
				case '[':
					num++;
					break;
				case ']':
					if (--num == 0)
					{
						return i;
					}
					break;
				}
			}
			return -1;
		}

		private static bool TryReadJsonString(string json, ref int index, out string value)
		{
			value = "";
			if (index >= json.Length || json[index] != '"')
			{
				return false;
			}
			index++;
			StringBuilder stringBuilder = new StringBuilder();
			while (index < json.Length)
			{
				char c = json[index++];
				switch (c)
				{
				case '"':
					value = stringBuilder.ToString();
					return true;
				default:
					stringBuilder.Append(c);
					break;
				case '\\':
					if (index >= json.Length)
					{
						return false;
					}
					switch (json[index++])
					{
					case '"':
						stringBuilder.Append('"');
						break;
					case '\\':
						stringBuilder.Append('\\');
						break;
					case '/':
						stringBuilder.Append('/');
						break;
					case 'b':
						stringBuilder.Append('\b');
						break;
					case 'f':
						stringBuilder.Append('\f');
						break;
					case 'n':
						stringBuilder.Append('\n');
						break;
					case 'r':
						stringBuilder.Append('\r');
						break;
					case 't':
						stringBuilder.Append('\t');
						break;
					case 'u':
					{
						if (index + 4 > json.Length)
						{
							return false;
						}
						if (!ushort.TryParse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
						{
							return false;
						}
						stringBuilder.Append((char)result);
						index += 4;
						break;
					}
					default:
						return false;
					}
					break;
				}
			}
			return false;
		}

		private static void SkipJsonWhitespace(string json, ref int index)
		{
			while (index < json.Length && char.IsWhiteSpace(json[index]))
			{
				index++;
			}
		}
	}
	public sealed class GeminiTranslateProvider : IUniversalTranslationProvider
	{
		private sealed class GeminiReply
		{
			public int Status;

			public string Text;

			public string Error;
		}

		private static readonly string[] Models = new string[2] { "gemini-2.5-flash", "gemini-2.5-flash-lite" };

		private const int ItemsPerRequest = 20;

		private const int CharsPerRequest = 12000;

		public string Name => "Gemini";

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			string key = ((settings != null) ? settings.GeminiApiKey : "");
			if (string.IsNullOrWhiteSpace(key))
			{
				result.Error = "Gemini: no API key";
				return result;
			}
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Gemini: empty batch";
				return result;
			}
			if (UrtOptEngine.IsCoolingDown(Name))
			{
				result.Error = "Gemini cooldown active (" + UrtOptEngine.CooldownRemainingSeconds(Name) + "s)";
				while (result.Translations.Count < texts.Count)
				{
					result.Translations.Add("");
				}
				return result;
			}
			string dst = LanguageCodeMapper.Canonical(target);
			string[] slots = new string[texts.Count];
			string lastErr = "";
			int start = 0;
			while (start < texts.Count)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int chars = 0;
				int end = start;
				StringBuilder prompt = new StringBuilder();
				prompt.Append("Translate each item to ").Append(dst).Append(". Return ONLY lines in the form id|translation. Keep placeholders like {0}, [TAG], <color> unchanged.\n");
				for (; end < texts.Count && end - start < 20; end++)
				{
					string text = texts[end] ?? "";
					if (end > start && chars + text.Length > 12000)
					{
						break;
					}
					prompt.Append(end - start).Append('|').Append(text.Replace("\r", " ").Replace("\n", " "))
						.Append('\n');
					chars += text.Length;
					result.CharacterCount += text.Length;
				}
				GeminiReply reply = await SendAsync(key.Trim(), prompt.ToString(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				string err;
				int status;
				Dictionary<int, string> map = ((reply != null) ? Apply(reply, out err, out status) : Fail(out err, out status));
				if (status != 0)
				{
					result.StatusCode = status;
				}
				if (map == null)
				{
					lastErr = err;
					if (status == 429 || status == 401 || status == 403)
					{
						break;
					}
				}
				else
				{
					for (int j = start; j < end; j++)
					{
						map.TryGetValue(j - start, out var tr);
						slots[j] = tr ?? "";
						tr = null;
					}
				}
				start = end;
				err = null;
			}
			for (int i = 0; i < texts.Count; i++)
			{
				result.Translations.Add(slots[i] ?? "");
			}
			result.Success = result.Translations.Exists((string t) => !string.IsNullOrWhiteSpace(t));
			if (!result.Success)
			{
				result.Error = (string.IsNullOrEmpty(lastErr) ? "Gemini: empty response" : lastErr);
			}
			return result;
		}

		private static Dictionary<int, string> Apply(GeminiReply reply, out string err, out int status)
		{
			status = reply.Status;
			err = reply.Error;
			if (reply.Text == null)
			{
				return null;
			}
			Dictionary<int, string> dictionary = new Dictionary<int, string>();
			string[] array = reply.Text.Replace("\r", "").Split(new char[1] { '\n' });
			foreach (string text in array)
			{
				int num = text.IndexOf('|');
				if (num > 0 && int.TryParse(text.Substring(0, num).Trim(), out var result))
				{
					dictionary[result] = text.Substring(num + 1).Trim();
				}
			}
			return dictionary;
		}

		private static Dictionary<int, string> Fail(out string err, out int status)
		{
			err = "Gemini: no response";
			status = 0;
			return null;
		}

		private static async Task<GeminiReply> SendAsync(string key, string prompt, CancellationToken ct)
		{
			string jsonBody = "{\"contents\":[{\"parts\":[{\"text\":\"" + ProviderJsonHelper.JsonEscape(prompt) + "\"}]}]}";
			GeminiReply reply = new GeminiReply
			{
				Error = "Gemini: no model answered"
			};
			for (int i = 0; i < Models.Length; i++)
			{
				string model = Models[i];
				try
				{
					string url = "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent";
					HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
					try
					{
						req.Content = (HttpContent)new StringContent(jsonBody, Encoding.UTF8, "application/json");
						((HttpHeaders)req.Headers).TryAddWithoutValidation("x-goog-api-key", key);
						HttpResponseMessage resp = await ((HttpMessageInvoker)ExtraHttp.Client).SendAsync(req, ct).ConfigureAwait(continueOnCapturedContext: false);
						try
						{
							string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
							reply.Status = (int)resp.StatusCode;
							if (resp.IsSuccessStatusCode)
							{
								List<string> parts = ProviderJsonHelper.ExtractStringValues(json, "text");
								if (parts.Count > 0)
								{
									reply.Text = string.Join("", parts.ToArray());
									reply.Error = null;
									return reply;
								}
								reply.Error = "Gemini: no text in response (" + model + ")";
								continue;
							}
							reply.Error = "Gemini HTTP " + reply.Status + " (" + model + ")";
							if (reply.Status == 429)
							{
								UrtOptEngine.CoolDown("Gemini", 60);
								return reply;
							}
							if (reply.Status == 401 || reply.Status == 403)
							{
								return reply;
							}
						}
						finally
						{
							((IDisposable)resp)?.Dispose();
						}
						goto end_IL_006e;
					}
					finally
					{
						((IDisposable)req)?.Dispose();
					}
					end_IL_006e:;
				}
				catch (OperationCanceledException)
				{
					if (ct.IsCancellationRequested)
					{
						throw;
					}
					reply.Error = "Gemini: timeout (" + model + ")";
				}
				catch (Exception ex)
				{
					reply.Error = "Gemini: " + ex.GetType().Name;
				}
			}
			return reply;
		}
	}
	public sealed class AzureTranslateProvider : IUniversalTranslationProvider
	{
		public const string ProviderName = "Azure Translator";

		private const string GlobalEndpoint = "https://api.cognitive.microsofttranslator.com/translate";

		public string Name => "Azure Translator";

		private static string ResolveEndpoint(string custom)
		{
			string text = (custom ?? "").Trim().TrimEnd(new char[1] { '/' });
			if (text.Length == 0)
			{
				return "https://api.cognitive.microsofttranslator.com/translate";
			}
			if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				text = "https://" + text;
			}
			if (text.IndexOf("/translate", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return text;
			}
			if (text.IndexOf("cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return text + "/translator/text/v3.0/translate";
			}
			return text + "/translate";
		}

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			string key = ((settings != null) ? (settings.AzureApiKey ?? "").Trim() : "");
			string region = ((settings != null && !string.IsNullOrWhiteSpace(settings.AzureRegion)) ? settings.AzureRegion.Trim() : "");
			if (key.Length == 0)
			{
				result.Error = "Azure: no API key";
				return result;
			}
			if (UrtOptEngine.IsCoolingDown("Azure Translator"))
			{
				result.Error = "Azure cooldown active (" + UrtOptEngine.CooldownRemainingSeconds("Azure Translator") + "s)";
				result.StatusCode = 0;
				return result;
			}
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Azure: empty batch";
				return result;
			}
			string to = LanguageCodeMapper.ToAzureCode(target);
			if (string.IsNullOrEmpty(to))
			{
				result.Error = "Azure: unsupported target language '" + target + "'";
				return result;
			}
			StringBuilder sb = new StringBuilder();
			sb.Append("[");
			for (int i = 0; i < texts.Count; i++)
			{
				if (i > 0)
				{
					sb.Append(",");
				}
				string raw = texts[i] ?? "";
				result.CharacterCount += raw.Length;
				sb.Append("{\"Text\":\"");
				sb.Append(ProviderJsonHelper.JsonEscape(raw));
				sb.Append("\"}");
			}
			sb.Append("]");
			string qs = "?api-version=3.0&to=" + Uri.EscapeDataString(to) + "&textType=plain";
			string from = (LanguageCodeMapper.IsAuto(source) ? "" : LanguageCodeMapper.ToAzureCode(source));
			if (!string.IsNullOrEmpty(from) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
			{
				qs = qs + "&from=" + Uri.EscapeDataString(from);
			}
			string body = sb.ToString();
			string endpoint = ResolveEndpoint((settings != null) ? settings.AzureEndpoint : "");
			bool sendRegion = region.Length > 0;
			try
			{
				string json = null;
				int code = 0;
				for (int attempt = 0; attempt < 2; attempt++)
				{
					HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint + qs);
					try
					{
						req.Content = (HttpContent)new StringContent(body, Encoding.UTF8, "application/json");
						((HttpHeaders)req.Headers).TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
						if (sendRegion)
						{
							((HttpHeaders)req.Headers).TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region);
						}
						HttpResponseMessage resp = await ((HttpMessageInvoker)ExtraHttp.Client).SendAsync(req, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						try
						{
							json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
							code = (int)resp.StatusCode;
							if (code == 429)
							{
								int wait = GetRetryAfterSeconds(resp);
								UrtOptEngine.CoolDown("Azure Translator", (wait > 0) ? wait : 20);
							}
						}
						finally
						{
							((IDisposable)resp)?.Dispose();
						}
					}
					finally
					{
						((IDisposable)req)?.Dispose();
					}
					if (code == 401 && sendRegion && attempt == 0 && string.Equals(region, "global", StringComparison.OrdinalIgnoreCase))
					{
						sendRegion = false;
						continue;
					}
					if ((code != 408 && (code < 500 || code > 599)) || attempt > 0)
					{
						break;
					}
					await Task.Delay(1000, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				result.StatusCode = code;
				if (code < 200 || code > 299)
				{
					string apiMessage = ProviderJsonHelper.ExtractFirstString(json, "message");
					switch (code)
					{
					case 429:
						result.Error = "Azure HTTP 429 (cooldown " + UrtOptEngine.CooldownRemainingSeconds("Azure Translator") + "s)";
						break;
					case 401:
						UrtOptEngine.CoolDown("Azure Translator", 600);
						result.Error = "Azure HTTP 401: key does not match region/endpoint" + (string.IsNullOrEmpty(region) ? " (region is empty)" : (" (region '" + region + "')")) + ". Set the resource Location (e.g. westeurope) or its Endpoint in settings.";
						break;
					case 403:
						UrtOptEngine.CoolDown("Azure Translator", 3600);
						result.Error = "Azure HTTP 403: quota exhausted or access denied";
						break;
					default:
						result.Error = "Azure HTTP " + code;
						break;
					}
					if (!string.IsNullOrWhiteSpace(apiMessage))
					{
						result.Error = result.Error + " - " + apiMessage;
					}
					while (result.Translations.Count < texts.Count)
					{
						result.Translations.Add("");
					}
					return result;
				}
				List<string> values = ProviderJsonHelper.ExtractStringValues(json ?? "", "text");
				for (int j = 0; j < values.Count && j < texts.Count; j++)
				{
					result.Translations.Add(values[j] ?? "");
				}
				while (result.Translations.Count < texts.Count)
				{
					result.Translations.Add("");
				}
				result.Success = result.Translations.Exists((string x) => !string.IsNullOrWhiteSpace(x));
				if (!result.Success)
				{
					string snip = ((json == null) ? "" : json.Replace("\r", " ").Replace("\n", " "));
					if (snip.Length > 160)
					{
						snip = snip.Substring(0, 160);
					}
					result.Error = "Azure: empty response " + snip;
				}
			}
			catch (OperationCanceledException)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				result.Error = "Azure: timeout";
			}
			catch (Exception ex)
			{
				result.Error = "Azure: " + ex.GetType().Name;
			}
			while (result.Translations.Count < texts.Count)
			{
				result.Translations.Add("");
			}
			return result;
		}

		private static int GetRetryAfterSeconds(HttpResponseMessage resp)
		{
			if (resp == null)
			{
				return 0;
			}
			try
			{
				IEnumerable<string> enumerable = default(IEnumerable<string>);
				if (((HttpHeaders)resp.Headers).TryGetValues("Retry-After", out enumerable))
				{
					foreach (string item in enumerable)
					{
						if (int.TryParse((item == null) ? "" : item.Trim(), out var result))
						{
							return Math.Max(1, Math.Min(120, result));
						}
						if (DateTimeOffset.TryParse(item, out var result2))
						{
							int num = (int)Math.Ceiling((result2 - DateTimeOffset.UtcNow).TotalSeconds);
							if (num > 0)
							{
								return Math.Min(120, num);
							}
						}
					}
				}
			}
			catch
			{
			}
			return 0;
		}
	}
	public sealed class MicrosoftEdgeTranslateProvider : IUniversalTranslationProvider
	{
		public const string ProviderName = "Microsoft Edge";

		private const string Endpoint = "https://edge.microsoft.com/translate/translatetext";

		private const int MaxItemChars = 5000;

		private const int MaxRequestChars = 15000;

		private const int MaxRequestItems = 50;

		public string Name => "Microsoft Edge";

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Microsoft Edge: empty batch";
				return result;
			}
			if (UrtOptEngine.IsCoolingDown("Microsoft Edge"))
			{
				result.Error = "Microsoft Edge cooldown active (" + UrtOptEngine.CooldownRemainingSeconds("Microsoft Edge") + "s)";
				while (result.Translations.Count < texts.Count)
				{
					result.Translations.Add("");
				}
				return result;
			}
			string to = LanguageCodeMapper.ToAzureCode(target);
			if (string.IsNullOrEmpty(to))
			{
				result.Error = "Microsoft Edge: unsupported target language '" + target + "'";
				return result;
			}
			string from = (LanguageCodeMapper.IsAuto(source) ? "" : LanguageCodeMapper.ToAzureCode(source));
			string qs = "?to=" + Uri.EscapeDataString(to);
			if (!string.IsNullOrEmpty(from) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
			{
				qs = qs + "&from=" + Uri.EscapeDataString(from);
			}
			string[] slots = new string[texts.Count];
			int index = 0;
			string lastError = "";
			while (index < texts.Count)
			{
				cancellationToken.ThrowIfCancellationRequested();
				List<int> ids = new List<int>();
				StringBuilder sb = new StringBuilder("[");
				int chars = 0;
				while (index < texts.Count && ids.Count < 50)
				{
					string t = texts[index] ?? "";
					if (t.Length > 5000)
					{
						index++;
						continue;
					}
					if (ids.Count > 0 && chars + t.Length > 15000)
					{
						break;
					}
					if (ids.Count > 0)
					{
						sb.Append(',');
					}
					sb.Append('"').Append(ProviderJsonHelper.JsonEscape(t)).Append('"');
					chars += t.Length;
					result.CharacterCount += t.Length;
					ids.Add(index);
					index++;
				}
				sb.Append(']');
				if (ids.Count == 0)
				{
					continue;
				}
				try
				{
					HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://edge.microsoft.com/translate/translatetext" + qs);
					try
					{
						req.Content = (HttpContent)new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
						HttpResponseMessage resp = await ((HttpMessageInvoker)ExtraHttp.BrowserClient).SendAsync(req, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						try
						{
							string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
							result.StatusCode = (int)resp.StatusCode;
							if (!resp.IsSuccessStatusCode)
							{
								lastError = "Microsoft Edge HTTP " + result.StatusCode;
								if (result.StatusCode == 429)
								{
									UrtOptEngine.CoolDown("Microsoft Edge", 60);
									break;
								}
								if (result.StatusCode != 401 && result.StatusCode != 403 && result.StatusCode != 404)
								{
									continue;
								}
								break;
							}
							List<string> values = ProviderJsonHelper.ExtractStringValues(json, "text");
							for (int j = 0; j < ids.Count && j < values.Count; j++)
							{
								slots[ids[j]] = values[j];
							}
						}
						finally
						{
							((IDisposable)resp)?.Dispose();
						}
						goto end_IL_03a3;
					}
					finally
					{
						((IDisposable)req)?.Dispose();
					}
					end_IL_03a3:;
				}
				catch (OperationCanceledException)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					lastError = "Microsoft Edge: timeout";
					break;
				}
				catch (Exception ex3)
				{
					Exception ex = ex3;
					lastError = "Microsoft Edge: " + ex.GetType().Name;
					break;
				}
			}
			for (int i = 0; i < texts.Count; i++)
			{
				result.Translations.Add(slots[i] ?? "");
			}
			result.Success = result.Translations.Exists((string x) => !string.IsNullOrWhiteSpace(x));
			if (!result.Success)
			{
				result.Error = (string.IsNullOrEmpty(lastError) ? "Microsoft Edge: empty response" : lastError);
			}
			return result;
		}
	}
	public sealed class CustomEndpointTranslateProvider : IUniversalTranslationProvider
	{
		public const string ProviderName = "Custom endpoint";

		public string Name => "Custom endpoint";

		public static bool IsOllama(TranslationProviderSettings settings)
		{
			return settings != null && string.Equals((settings.CustomEndpointType ?? "").Trim(), "Ollama", StringComparison.OrdinalIgnoreCase);
		}

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Custom endpoint: empty batch";
				return result;
			}
			string url = ((settings != null) ? (settings.CustomEndpointUrl ?? "").Trim().TrimEnd(new char[1] { '/' }) : "");
			if (url.Length == 0)
			{
				url = (IsOllama(settings) ? "http://localhost:11434" : "http://localhost:5000");
			}
			try
			{
				if (!IsOllama(settings))
				{
					await TranslateLibreAsync(url, texts, source, target, settings, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					await TranslateOllamaAsync(url, texts, target, settings, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (OperationCanceledException)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				UrtOptEngine.CoolDown("Custom endpoint", 600);
				result.Error = "Custom endpoint: timeout";
			}
			catch (Exception ex)
			{
				UrtOptEngine.CoolDown("Custom endpoint", 600);
				result.Error = "Custom endpoint: " + ex.GetType().Name + " " + ex.Message;
			}
			while (result.Translations.Count < texts.Count)
			{
				result.Translations.Add("");
			}
			result.Success = result.Translations.Exists((string x) => !string.IsNullOrWhiteSpace(x));
			if (!result.Success && string.IsNullOrEmpty(result.Error))
			{
				result.Error = "Custom endpoint: empty response";
			}
			return result;
		}

		private static string LibreCode(string language)
		{
			string text = LanguageCodeMapper.ToGoogleCode(language).ToLowerInvariant();
			return text switch
			{
				"zh-cn" => "zh", 
				"zh-tw" => "zt", 
				"pt-pt" => "pt", 
				"pt-br" => "pb", 
				_ => text, 
			};
		}

		private static async Task TranslateLibreAsync(string url, IList<string> texts, string source, string target, TranslationProviderSettings settings, TranslationProviderResult result, CancellationToken ct)
		{
			StringBuilder sb = new StringBuilder("{\"q\":[");
			for (int i = 0; i < texts.Count; i++)
			{
				if (i > 0)
				{
					sb.Append(',');
				}
				sb.Append('"').Append(ProviderJsonHelper.JsonEscape(texts[i] ?? "")).Append('"');
				result.CharacterCount += (texts[i] ?? "").Length;
			}
			string src = (LanguageCodeMapper.IsAuto(source) ? "auto" : LibreCode(source));
			if (string.IsNullOrEmpty(src))
			{
				src = "auto";
			}
			sb.Append("],\"source\":\"").Append(src).Append("\",\"target\":\"")
				.Append(LibreCode(target))
				.Append("\",\"format\":\"text\"");
			if (settings != null && !string.IsNullOrWhiteSpace(settings.CustomEndpointKey))
			{
				sb.Append(",\"api_key\":\"").Append(ProviderJsonHelper.JsonEscape(settings.CustomEndpointKey.Trim())).Append('"');
			}
			sb.Append('}');
			string endpoint = (url.EndsWith("/translate", StringComparison.OrdinalIgnoreCase) ? url : (url + "/translate"));
			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint);
			try
			{
				req.Content = (HttpContent)new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
				HttpResponseMessage resp = await ((HttpMessageInvoker)ExtraHttp.Client).SendAsync(req, ct).ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
					result.StatusCode = (int)resp.StatusCode;
					if (!resp.IsSuccessStatusCode)
					{
						string err = ProviderJsonHelper.ExtractFirstString(json, "error");
						result.Error = "LibreTranslate HTTP " + result.StatusCode + (string.IsNullOrEmpty(err) ? "" : (": " + err));
						return;
					}
					List<string> values = ProviderJsonHelper.ExtractStringValues(json, "translatedText");
					for (int j = 0; j < texts.Count; j++)
					{
						result.Translations.Add((j < values.Count) ? (values[j] ?? "") : "");
					}
				}
				finally
				{
					((IDisposable)resp)?.Dispose();
				}
			}
			finally
			{
				((IDisposable)req)?.Dispose();
			}
		}

		private static async Task TranslateOllamaAsync(string url, IList<string> texts, string target, TranslationProviderSettings settings, TranslationProviderResult result, CancellationToken ct)
		{
			string model = ((settings != null && !string.IsNullOrWhiteSpace(settings.CustomEndpointModel)) ? settings.CustomEndpointModel.Trim() : "qwen2.5:7b");
			string dst = LanguageCodeMapper.Canonical(target);
			string[] slots = new string[texts.Count];
			for (int start = 0; start < texts.Count; start += 10)
			{
				ct.ThrowIfCancellationRequested();
				int end = Math.Min(texts.Count, start + 10);
				StringBuilder prompt = new StringBuilder();
				prompt.Append("You are a translator for the video game RimWorld. Translate each item into ").Append(dst).Append(". Keep placeholders like {0}, {PAWN_name}, [TAG], <color=...> and \\n unchanged. ")
					.Append("Keep names of people, places and mods as they are. ")
					.Append("Answer ONLY with lines in the form id|translation, one line per item, nothing else.\n");
				for (int j = start; j < end; j++)
				{
					string t = (texts[j] ?? "").Replace("\r", "").Replace("\n", "\\n").Replace('|', '¦');
					prompt.Append(j - start).Append('|').Append(t)
						.Append('\n');
					result.CharacterCount += t.Length;
				}
				string body = "{\"model\":\"" + ProviderJsonHelper.JsonEscape(model) + "\",\"stream\":false,\"keep_alive\":\"30m\",\"options\":{\"temperature\":0},\"prompt\":\"" + ProviderJsonHelper.JsonEscape(prompt.ToString()) + "\"}";
				string endpoint = (url.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase) ? url : (url + "/api/generate"));
				HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint);
				try
				{
					req.Content = (HttpContent)new StringContent(body, Encoding.UTF8, "application/json");
					HttpResponseMessage resp = await ((HttpMessageInvoker)ExtraHttp.Client).SendAsync(req, ct).ConfigureAwait(continueOnCapturedContext: false);
					try
					{
						string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
						result.StatusCode = (int)resp.StatusCode;
						if (!resp.IsSuccessStatusCode)
						{
							string err = ProviderJsonHelper.ExtractFirstString(json, "error");
							result.Error = "Ollama HTTP " + result.StatusCode + (string.IsNullOrEmpty(err) ? "" : (": " + err));
							break;
						}
						string answer = ProviderJsonHelper.ExtractFirstString(json, "response") ?? "";
						string[] array = answer.Replace("\r", "").Split(new char[1] { '\n' });
						foreach (string line in array)
						{
							int bar = line.IndexOf('|');
							if (bar > 0 && int.TryParse(line.Substring(0, bar).Trim(), out var id) && id >= 0 && start + id < end && slots[start + id] == null)
							{
								string value = line.Substring(bar + 1).Trim();
								int again = value.IndexOf('|');
								if (again >= 0)
								{
									value = value.Substring(0, again).Trim();
								}
								slots[start + id] = value.Replace("\\n", "\n").Replace('¦', '|');
							}
						}
					}
					finally
					{
						((IDisposable)resp)?.Dispose();
					}
					continue;
				}
				finally
				{
					((IDisposable)req)?.Dispose();
				}
			}
			for (int i = 0; i < texts.Count; i++)
			{
				result.Translations.Add(slots[i] ?? "");
			}
		}
	}
	public sealed class GoogleTranslateProvider
	{
		private sealed class PlaceholderMap
		{
			public readonly List<string> Original = new List<string>();

			public readonly List<string> Token = new List<string>();
		}

		private readonly HttpClient client = CreateClient();

		private const string Endpoint = "https://translation.googleapis.com/language/translate/v2";

		private const int MaxTextsPerRequest = 128;

		private static HttpClient CreateClient()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected O, but got Unknown
			HttpClient val = new HttpClient();
			val.Timeout = TimeSpan.FromSeconds(10.0);
			return val;
		}

		public async Task<DeepLBatchResult> TranslateBatchAsync(string apiKey, IList<string> texts, string source, string target, int maxRetries = 3, CancellationToken cancellationToken = default(CancellationToken))
		{
			DeepLBatchResult result = new DeepLBatchResult();
			if (string.IsNullOrWhiteSpace(apiKey))
			{
				result.Error = "Google API key is empty.";
				return result;
			}
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Batch is empty.";
				return result;
			}
			string targetCode = Code(target);
			if (string.IsNullOrEmpty(targetCode))
			{
				result.Error = "Unsupported Google target language: " + (target ?? "(empty)");
				return result;
			}
			string sourceCode = Code(source);
			List<string> allTranslations = new List<string>();
			for (int offset = 0; offset < texts.Count; offset += 128)
			{
				int count = Math.Min(128, texts.Count - offset);
				List<string> requestTexts = new List<string>();
				List<PlaceholderMap> placeholders = new List<PlaceholderMap>();
				for (int i = 0; i < count; i++)
				{
					PlaceholderMap map = new PlaceholderMap();
					requestTexts.Add(ProtectPlaceholders(texts[offset + i] ?? "", map));
					placeholders.Add(map);
				}
				bool requestSucceeded = false;
				for (int attempt = 1; attempt <= maxRetries + 1; attempt++)
				{
					result.Attempts = Math.Max(result.Attempts, attempt);
					try
					{
						string url = "https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(apiKey);
						List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>();
						foreach (string text2 in requestTexts)
						{
							fields.Add(new KeyValuePair<string, string>("q", text2 ?? ""));
						}
						fields.Add(new KeyValuePair<string, string>("target", targetCode));
						fields.Add(new KeyValuePair<string, string>("format", "text"));
						if (!string.IsNullOrEmpty(sourceCode))
						{
							fields.Add(new KeyValuePair<string, string>("source", sourceCode));
						}
						HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
						try
						{
							request.Content = (HttpContent)new FormUrlEncodedContent((IEnumerable<KeyValuePair<string, string>>)fields);
							HttpResponseMessage response = await ((HttpMessageInvoker)client).SendAsync(request, cancellationToken);
							try
							{
								string body = await response.Content.ReadAsStringAsync();
								result.StatusCode = (int)response.StatusCode;
								if (result.RawResponses == null)
								{
									result.RawResponses = new List<string>();
								}
								result.RawResponses.Add((body == null) ? "" : ((body.Length <= 2500) ? body : (body.Substring(0, 2500) + "...")));
								if (response.IsSuccessStatusCode)
								{
									List<string> translated = ExtractTranslationTexts(body);
									if (translated.Count != requestTexts.Count)
									{
										result.Error = "Google returned " + translated.Count + " translations for " + requestTexts.Count + " texts.";
									}
									else
									{
										for (int j = 0; j < translated.Count; j++)
										{
											allTranslations.Add(RestorePlaceholders(translated[j], placeholders[j]));
										}
										requestSucceeded = true;
									}
								}
								else
								{
									if (IsRetryable(response.StatusCode) && attempt <= maxRetries)
									{
										await Task.Delay(Backoff(attempt), cancellationToken);
										continue;
									}
									result.Error = "HTTP " + (int)response.StatusCode + ": " + body;
								}
							}
							finally
							{
								((IDisposable)response)?.Dispose();
							}
						}
						finally
						{
							((IDisposable)request)?.Dispose();
						}
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch (Exception ex)
					{
						if (attempt <= maxRetries)
						{
							await Task.Delay(Backoff(attempt), cancellationToken);
							continue;
						}
						result.Error = ex.GetType().Name + ": " + ex.Message;
					}
					break;
				}
				if (!requestSucceeded)
				{
					return result;
				}
			}
			result.Translations = allTranslations;
			result.Success = true;
			foreach (string text in texts)
			{
				result.CharacterCount += (text ?? "").Length;
			}
			return result;
		}

		private static bool IsRetryable(HttpStatusCode code)
		{
			return code == (HttpStatusCode)429 || code == HttpStatusCode.InternalServerError || code == HttpStatusCode.BadGateway || code == HttpStatusCode.ServiceUnavailable || code == HttpStatusCode.GatewayTimeout;
		}

		private static int Backoff(int attempt)
		{
			return Math.Min(10000, 1000 * (1 << Math.Max(0, attempt - 1)));
		}

		private static string Code(string language)
		{
			switch ((language ?? "").Trim().ToLowerInvariant())
			{
			case "english":
			case "en":
				return "en";
			case "czech":
			case "cs":
				return "cs";
			case "german":
			case "de":
				return "de";
			case "french":
			case "fr":
				return "fr";
			case "italian":
			case "it":
				return "it";
			case "japanese":
			case "ja":
				return "ja";
			case "korean":
			case "ko":
				return "ko";
			case "polish":
			case "pl":
				return "pl";
			case "portuguese":
			case "pt-pt":
				return "pt-PT";
			case "pt":
				return "pt";
			case "portuguesebrazilian":
			case "pt-br":
				return "pt";
			case "russian":
			case "ru":
				return "ru";
			case "spanish":
			case "es":
				return "es";
			case "dutch":
			case "nl":
				return "nl";
			case "danish":
			case "da":
				return "da";
			case "swedish":
			case "sv":
				return "sv";
			case "norwegian":
			case "nb":
				return "no";
			case "ukrainian":
			case "uk":
				return "uk";
			case "chinesesimplified":
			case "zh":
				return "zh-CN";
			case "chinesetraditional":
			case "zh-hant":
				return "zh-TW";
			case "turkish":
			case "tr":
				return "tr";
			case "vietnamese":
			case "vi":
				return "vi";
			default:
				return "";
			}
		}

		private static string ExtractTranslationText(string json, int start)
		{
			StringBuilder stringBuilder = new StringBuilder();
			bool flag = false;
			for (int i = start; i < json.Length; i++)
			{
				char c = json[i];
				if (flag)
				{
					switch (c)
					{
					case '"':
						stringBuilder.Append('"');
						break;
					case '\\':
						stringBuilder.Append('\\');
						break;
					case '/':
						stringBuilder.Append('/');
						break;
					case 'b':
						stringBuilder.Append('\b');
						break;
					case 'f':
						stringBuilder.Append('\f');
						break;
					case 'n':
						stringBuilder.Append('\n');
						break;
					case 'r':
						stringBuilder.Append('\r');
						break;
					case 't':
						stringBuilder.Append('\t');
						break;
					case 'u':
						if (i + 4 < json.Length)
						{
							string s = json.Substring(i + 1, 4);
							if (ushort.TryParse(s, NumberStyles.HexNumber, null, out var result))
							{
								stringBuilder.Append((char)result);
								i += 4;
							}
						}
						break;
					default:
						stringBuilder.Append(c);
						break;
					}
					flag = false;
				}
				else
				{
					switch (c)
					{
					case '\\':
						flag = true;
						break;
					case '"':
						return stringBuilder.ToString();
					default:
						stringBuilder.Append(c);
						break;
					}
				}
			}
			return null;
		}

		private static List<string> ExtractTranslationTexts(string json)
		{
			List<string> list = new List<string>();
			if (string.IsNullOrEmpty(json))
			{
				return list;
			}
			int num = 0;
			while (num < json.Length)
			{
				int num2 = json.IndexOf("\"translatedText\"", num, StringComparison.Ordinal);
				if (num2 < 0)
				{
					break;
				}
				int i;
				for (i = num2 + "\"translatedText\"".Length; i < json.Length && char.IsWhiteSpace(json[i]); i++)
				{
				}
				if (i >= json.Length || json[i] != ':')
				{
					num = num2 + "\"translatedText\"".Length;
					continue;
				}
				for (i++; i < json.Length && char.IsWhiteSpace(json[i]); i++)
				{
				}
				if (i >= json.Length || json[i] != '"')
				{
					num = num2 + "\"translatedText\"".Length;
					continue;
				}
				string text = ExtractTranslationText(json, i + 1);
				if (text == null)
				{
					num = i + 1;
					continue;
				}
				list.Add(text);
				num = i + 1;
			}
			return list;
		}

		private static string ProtectPlaceholders(string text, PlaceholderMap map)
		{
			StringBuilder stringBuilder = new StringBuilder();
			int num = 0;
			while (num < text.Length)
			{
				if (text[num] == '{')
				{
					int num2 = text.IndexOf('}', num + 1);
					if (num2 >= 0)
					{
						string item = text.Substring(num, num2 - num + 1);
						string text2 = "RIMTOKEN" + map.Token.Count + "XYZ";
						map.Original.Add(item);
						map.Token.Add(text2);
						stringBuilder.Append(text2);
						num = num2 + 1;
						continue;
					}
				}
				stringBuilder.Append(text[num]);
				num++;
			}
			return stringBuilder.ToString();
		}

		private static string RestorePlaceholders(string text, PlaceholderMap map)
		{
			string text2 = text ?? "";
			for (int i = 0; i < map.Token.Count; i++)
			{
				text2 = text2.Replace(map.Token[i], map.Original[i]);
			}
			return text2;
		}
	}
	public static class LanguageCodeMapper
	{
		private static readonly Dictionary<string, string> Codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "English", "EN" },
			{ "Czech", "CS" },
			{ "German", "DE" },
			{ "French", "FR" },
			{ "Italian", "IT" },
			{ "Spanish", "ES" },
			{ "SpanishLatin", "ES" },
			{ "Portuguese", "PT" },
			{ "PortugueseBrazilian", "PT-BR" },
			{ "Polish", "PL" },
			{ "Russian", "RU" },
			{ "Ukrainian", "UK" },
			{ "Dutch", "NL" },
			{ "Danish", "DA" },
			{ "Swedish", "SV" },
			{ "Norwegian", "NB" },
			{ "Finnish", "FI" },
			{ "Turkish", "TR" },
			{ "Japanese", "JA" },
			{ "Korean", "KO" },
			{ "ChineseSimplified", "ZH" },
			{ "ChineseTraditional", "ZH" },
			{ "Chinese", "ZH" },
			{ "SChinese", "ZH" },
			{ "TChinese", "ZH" },
			{ "Vietnamese", "VI" },
			{ "Hungarian", "HU" },
			{ "Romanian", "RO" },
			{ "Slovak", "SK" },
			{ "Bulgarian", "BG" },
			{ "Greek", "EL" },
			{ "Hebrew", "HE" },
			{ "Indonesian", "ID" },
			{ "Latvian", "LV" },
			{ "Lithuanian", "LT" },
			{ "Estonian", "ET" },
			{ "Arabic", "AR" },
			{ "Catalan", "CA" },
			{ "Croatian", "HR" },
			{ "Serbian", "SR" },
			{ "Slovenian", "SL" },
			{ "Thai", "TH" },
			{ "Hindi", "HI" }
		};

		private static readonly string[] CommonLanguages = new string[40]
		{
			"Czech", "English", "German", "French", "Spanish", "Italian", "Polish", "Russian", "Ukrainian", "Portuguese",
			"PortugueseBrazilian", "Dutch", "Danish", "Swedish", "Norwegian", "Finnish", "Turkish", "Japanese", "Korean", "ChineseSimplified",
			"ChineseTraditional", "Vietnamese", "Hungarian", "Romanian", "Slovak", "Bulgarian", "Greek", "Hebrew", "Indonesian", "Latvian",
			"Lithuanian", "Estonian", "SpanishLatin", "Catalan", "Croatian", "Serbian", "Slovenian", "Arabic", "Thai", "Hindi"
		};

		private static readonly ConcurrentDictionary<string, string> CanonicalCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		public static string[] GetCommonLanguages()
		{
			return (string[])CommonLanguages.Clone();
		}

		public static string Canonical(string language)
		{
			if (string.IsNullOrWhiteSpace(language))
			{
				return "";
			}
			if (CanonicalCache.TryGetValue(language, out var value))
			{
				return value;
			}
			string text = CanonicalCore(language);
			if (CanonicalCache.Count > 4096)
			{
				CanonicalCache.Clear();
			}
			CanonicalCache.TryAdd(language, text);
			return text;
		}

		private static string CanonicalCore(string language)
		{
			string text = language.Trim();
			int num = text.IndexOf(" (", StringComparison.Ordinal);
			if (num > 0)
			{
				text = text.Substring(0, num).Trim();
			}
			if (string.Equals(text, "Chinese", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "SChinese", StringComparison.OrdinalIgnoreCase) || text.IndexOf("Simplified", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return "ChineseSimplified";
			}
			if (string.Equals(text, "TChinese", StringComparison.OrdinalIgnoreCase) || text.IndexOf("Traditional", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return "ChineseTraditional";
			}
			foreach (KeyValuePair<string, string> code in Codes)
			{
				if (string.Equals(code.Key, text, StringComparison.OrdinalIgnoreCase))
				{
					return code.Key;
				}
			}
			return text;
		}

		public static string ToDeepLCode(string language)
		{
			string text = Canonical(language);
			if (Codes.TryGetValue(text, out var value))
			{
				return value;
			}
			return text;
		}

		public static string ToDeepLSourceCode(string language)
		{
			string text = Canonical(language);
			if (string.IsNullOrEmpty(text) || IsAuto(text))
			{
				return "";
			}
			string text2 = ToDeepLCode(text);
			if (text2 == null)
			{
				return "";
			}
			int num = text2.IndexOf('-');
			if (num > 0)
			{
				text2 = text2.Substring(0, num);
			}
			return (text2.Length <= 3) ? text2.ToUpperInvariant() : "";
		}

		public static string ToDeepLTargetCode(string language)
		{
			string text = Canonical(language);
			switch (text.ToLowerInvariant())
			{
			case "english":
			case "en":
				return "EN-US";
			case "portuguese":
			case "pt-pt":
				return "PT-PT";
			case "portuguesebrazilian":
			case "pt":
			case "pt-br":
				return "PT-BR";
			case "chinesesimplified":
			case "zh":
			case "zh-cn":
			case "zh-hans":
				return "ZH-HANS";
			case "chinesetraditional":
			case "zh-tw":
			case "zh-hant":
				return "ZH-HANT";
			case "norwegian":
			case "no":
			case "nb":
				return "NB";
			default:
			{
				string text2 = ToDeepLCode(text);
				return string.IsNullOrEmpty(text2) ? "" : text2.ToUpperInvariant();
			}
			}
		}

		public static string ToProviderCode(string language)
		{
			string text = ToGoogleCode(language);
			return string.IsNullOrWhiteSpace(text) ? "en" : text.ToLowerInvariant();
		}

		public static string ToGoogleCode(string language)
		{
			string text = Canonical(language);
			return text.ToLowerInvariant() switch
			{
				"english" => "en", 
				"czech" => "cs", 
				"german" => "de", 
				"french" => "fr", 
				"italian" => "it", 
				"spanish" => "es", 
				"spanishlatin" => "es", 
				"polish" => "pl", 
				"russian" => "ru", 
				"ukrainian" => "uk", 
				"portuguese" => "pt-PT", 
				"portuguesebrazilian" => "pt", 
				"dutch" => "nl", 
				"danish" => "da", 
				"swedish" => "sv", 
				"norwegian" => "no", 
				"finnish" => "fi", 
				"turkish" => "tr", 
				"japanese" => "ja", 
				"korean" => "ko", 
				"chinesesimplified" => "zh-CN", 
				"chinesetraditional" => "zh-TW", 
				"vietnamese" => "vi", 
				"hungarian" => "hu", 
				"romanian" => "ro", 
				"slovak" => "sk", 
				"bulgarian" => "bg", 
				"greek" => "el", 
				"hebrew" => "he", 
				"indonesian" => "id", 
				"latvian" => "lv", 
				"lithuanian" => "lt", 
				"estonian" => "et", 
				"arabic" => "ar", 
				"catalan" => "ca", 
				"croatian" => "hr", 
				"serbian" => "sr", 
				"slovenian" => "sl", 
				"thai" => "th", 
				"hindi" => "hi", 
				_ => LooksLikeCode(text) ? text : "", 
			};
		}

		public static string ToAzureCode(string language)
		{
			string text = ToGoogleCode(language).ToLowerInvariant();
			switch (text)
			{
			case "zh":
			case "zh-cn":
			case "zh-hans":
				return "zh-Hans";
			case "zh-tw":
			case "zh-hant":
				return "zh-Hant";
			case "no":
			case "nb":
				return "nb";
			case "pt-pt":
				return "pt-pt";
			case "pt":
			case "pt-br":
				return "pt";
			case "sr":
				return "sr-Cyrl";
			default:
				return text;
			}
		}

		public static string ToBaseCode(string language)
		{
			string text = ToGoogleCode(language).ToLowerInvariant();
			int num = text.IndexOf('-');
			return (num > 0) ? text.Substring(0, num) : text;
		}

		public static bool IsAuto(string language)
		{
			string text = (language ?? "").Trim();
			return text.Length == 0 || string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase);
		}

		private static bool LooksLikeCode(string value)
		{
			if (string.IsNullOrEmpty(value) || value.Length < 2 || value.Length > 7)
			{
				return false;
			}
			foreach (char c in value)
			{
				if ((!char.IsLetter(c) || c >= '\u0080') && c != '-' && c != '_')
				{
					return false;
				}
			}
			return char.IsLetter(value[0]) && char.IsLetter(value[1]) && (value.Length == 2 || value[2] == '-' || value[2] == '_');
		}

		public static bool IsSupported(string language)
		{
			return Codes.ContainsKey(Canonical(language));
		}
	}
	public static class LanguageDetector
	{
		private enum ScriptKind
		{
			Hangul,
			Kana,
			Cjk,
			Cyrillic
		}

		private static readonly Regex CzechWordShape;

		private static readonly Regex CzechWords;

		private static readonly HashSet<string> CommonLatinWords;

		private static readonly HashSet<string> EnglishResidualSet;

		private static readonly string[] EnglishResiduals;

		public static string DetectEmbeddedSourceLanguage(string text)
		{
			string text2 = (text ?? "").Trim();
			if (string.IsNullOrEmpty(text2))
			{
				return "";
			}
			string text3 = DetectScriptLanguage(text2);
			if (!string.IsNullOrEmpty(text3))
			{
				return text3;
			}
			if (LooksLikeCzech(text2))
			{
				return "Czech";
			}
			if (ContainsAny(text2, "ąćęłńśźż"))
			{
				return "Polish";
			}
			if (ContainsAny(text2, "äöüß"))
			{
				return "German";
			}
			if (text2.IndexOf('ñ') >= 0 || text2.IndexOf('Ñ') >= 0)
			{
				return "Spanish";
			}
			if (ContainsAny(text2, "ãõáê"))
			{
				return "Portuguese";
			}
			return "";
		}

		public static bool TooShortForProvider(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return true;
			}
			string text2 = text.Trim();
			foreach (char c in text2)
			{
				if (c >= '\u3040' && c <= '\u9fff')
				{
					return false;
				}
				if (c >= '가' && c <= '\ud7af')
				{
					return false;
				}
			}
			int num = 0;
			for (int j = 0; j < text2.Length; j++)
			{
				if (char.IsLetter(text2[j]))
				{
					num++;
				}
			}
			return num < 5;
		}

		public static bool LooksLikeTargetText(string text, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = LanguageCodeMapper.Canonical(targetLanguage);
			if (string.IsNullOrEmpty(text2))
			{
				return false;
			}
			if (HasResidualForeignScript(text, text2) || HasResidualEnglish(text, text2) || HasLeftoverLatin(text))
			{
				return false;
			}
			if (IsMixedLanguage(text, text2))
			{
				return false;
			}
			if (string.Equals(text2, "Czech", StringComparison.OrdinalIgnoreCase))
			{
				return LooksLikeCzech(text);
			}
			string text3 = DetectEmbeddedSourceLanguage(text);
			if (!string.IsNullOrEmpty(text3) && string.Equals(LanguageCodeMapper.Canonical(text3), text2, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (string.Equals(text2, "Russian", StringComparison.OrdinalIgnoreCase) || string.Equals(text2, "Ukrainian", StringComparison.OrdinalIgnoreCase) || string.Equals(text2, "Bulgarian", StringComparison.OrdinalIgnoreCase))
			{
				return HasScript(text, ScriptKind.Cyrillic);
			}
			if (string.Equals(text2, "Korean", StringComparison.OrdinalIgnoreCase))
			{
				return HasScript(text, ScriptKind.Hangul);
			}
			if (string.Equals(text2, "Japanese", StringComparison.OrdinalIgnoreCase))
			{
				return HasScript(text, ScriptKind.Kana) || HasScript(text, ScriptKind.Cjk);
			}
			if (string.Equals(text2, "ChineseSimplified", StringComparison.OrdinalIgnoreCase) || string.Equals(text2, "ChineseTraditional", StringComparison.OrdinalIgnoreCase))
			{
				return HasScript(text, ScriptKind.Cjk);
			}
			if (IsLatinTarget(text2))
			{
				return HasLatinLetter(text);
			}
			return TextHasTargetScript(text, text2);
		}

		public static bool EnglishHeuristicsApply(string targetLanguage)
		{
			string text = LanguageCodeMapper.Canonical(targetLanguage ?? "");
			if (string.Equals(text, "English", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (string.Equals(text, "Czech", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Slovak", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return !IsLatinTarget(text);
		}

		private static bool DetectedConflictsWithTarget(string text, string detected, string target)
		{
			if (string.IsNullOrEmpty(detected))
			{
				return false;
			}
			string a = LanguageCodeMapper.Canonical(detected);
			string text2 = LanguageCodeMapper.Canonical(target);
			if (string.Equals(a, text2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string value = DetectScriptLanguage(text);
			if (!string.IsNullOrEmpty(value))
			{
				return !TextHasTargetScript(text, text2);
			}
			if (string.Equals(a, "Czech", StringComparison.OrdinalIgnoreCase))
			{
				return !string.Equals(text2, "Slovak", StringComparison.OrdinalIgnoreCase);
			}
			return string.Equals(text2, "English", StringComparison.OrdinalIgnoreCase) || !IsLatinTarget(text2);
		}

		public static bool TextHasTargetScript(string text, string targetLanguage)
		{
			string text2 = LanguageCodeMapper.Canonical(targetLanguage ?? "");
			if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text2))
			{
				return false;
			}
			if (IsLatinTarget(text2) || string.Equals(text2, "English", StringComparison.OrdinalIgnoreCase))
			{
				return HasLatinLetter(text);
			}
			for (int i = 0; i < text.Length; i++)
			{
				if (char.IsLetter(text[i]) && ScriptMatchesTarget(text[i], text2))
				{
					return true;
				}
			}
			return false;
		}

		private static bool IsLatinTarget(string target)
		{
			switch ((target ?? "").ToLowerInvariant())
			{
			case "russian":
			case "ukrainian":
			case "bulgarian":
			case "serbian":
			case "macedonian":
			case "belarusian":
			case "korean":
			case "japanese":
			case "chinesesimplified":
			case "chinesetraditional":
			case "arabic":
			case "greek":
			case "hebrew":
			case "thai":
			case "hindi":
			case "english":
			case "":
				return false;
			default:
				return true;
			}
		}

		private static bool HasLatinLetter(string text)
		{
			foreach (char c in text)
			{
				if (char.IsLetter(c) && c <= 'ɏ')
				{
					return true;
				}
			}
			return false;
		}

		public static bool ContainsEnglishWords(string text)
		{
			return HasResidualEnglish(text, "Czech");
		}

		public static bool LooksLikeCzech(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			if (HasResidualForeignScript(text, "Czech"))
			{
				return false;
			}
			if (HasResidualEnglish(text, "Czech"))
			{
				return false;
			}
			if (IsMixedLanguage(text, "Czech"))
			{
				return false;
			}
			string text2 = text.ToLowerInvariant();
			int num = 0;
			int num2 = 0;
			string text3 = text2;
			foreach (char value in text3)
			{
				if ("ěščřžůďťň".IndexOf(value) >= 0)
				{
					num++;
				}
				if ("áéíóúý".IndexOf(value) >= 0)
				{
					num2++;
				}
			}
			return num > 0 || num2 >= 2 || CzechWordShape.IsMatch(text2) || CzechWords.IsMatch(text2);
		}

		public static bool LooksLikeProperNameOrCode(string text)
		{
			string text2 = (text ?? "").Trim();
			if (text2.Length == 0)
			{
				return false;
			}
			int num = 0;
			int num2 = 0;
			string text3 = text2;
			foreach (char c in text3)
			{
				if (char.IsLetter(c))
				{
					num++;
				}
				else if (char.IsDigit(c))
				{
					num2++;
				}
			}
			if (num == 0)
			{
				return true;
			}
			if (LooksLikeCzech(text2))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(DetectScriptLanguage(text2)))
			{
				return false;
			}
			if (ContainsEnglishWords(text2))
			{
				return false;
			}
			string[] array = text2.Split(new char[3] { ' ', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
			if (array.Length != 0 && ContainsEnglishWords(array[0]))
			{
				return false;
			}
			string[] array2 = text2.Split(new char[3] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
			if (array2.Length == 0)
			{
				return false;
			}
			string[] array3 = array2;
			foreach (string item in array3)
			{
				if (CommonLatinWords.Contains(item))
				{
					return false;
				}
			}
			return (num2 > 0 || text2.IndexOf('_') >= 0 || (array2.Length >= 2 && array2.Length <= 4 && char.IsUpper(array2[0][0]))) && num <= 28 && array2.Length <= 4;
		}

		private static string DetectScriptLanguage(string text)
		{
			bool flag = false;
			bool flag2 = false;
			bool flag3 = false;
			bool flag4 = false;
			bool flag5 = false;
			bool flag6 = false;
			bool flag7 = false;
			foreach (char c in text)
			{
				int num = c;
				if ((num >= 44032 && num <= 55215) || (num >= 4352 && num <= 4607))
				{
					flag = true;
				}
				else if ((num >= 12352 && num <= 12447) || (num >= 12448 && num <= 12543))
				{
					flag2 = true;
				}
				else if ((num >= 13312 && num <= 19903) || (num >= 19968 && num <= 40959))
				{
					flag3 = true;
				}
				else if (num >= 1536 && num <= 1791)
				{
					flag4 = true;
				}
				else if (num >= 1024 && num <= 1279)
				{
					flag5 = true;
				}
				else if (num >= 880 && num <= 1023)
				{
					flag6 = true;
				}
				else if (num >= 1424 && num <= 1535)
				{
					flag7 = true;
				}
			}
			if (flag)
			{
				return "Korean";
			}
			if (flag2)
			{
				return "Japanese";
			}
			if (flag3)
			{
				return "ChineseSimplified";
			}
			if (flag5)
			{
				return "Russian";
			}
			if (flag4)
			{
				return "Arabic";
			}
			if (flag6)
			{
				return "Greek";
			}
			if (flag7)
			{
				return "Hebrew";
			}
			return "";
		}

		private static bool HasScript(string text, ScriptKind kind)
		{
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				int num = c;
				if (kind == ScriptKind.Hangul && ((num >= 44032 && num <= 55215) || (num >= 4352 && num <= 4607)))
				{
					return true;
				}
				if (kind == ScriptKind.Kana && ((num >= 12352 && num <= 12447) || (num >= 12448 && num <= 12543)))
				{
					return true;
				}
				if (kind == ScriptKind.Cjk && ((num >= 13312 && num <= 19903) || (num >= 19968 && num <= 40959)))
				{
					return true;
				}
				if (kind == ScriptKind.Cyrillic && num >= 1024 && num <= 1279)
				{
					return true;
				}
			}
			return false;
		}

		private static bool ContainsAny(string text, string chars)
		{
			foreach (char c in text)
			{
				if (chars.IndexOf(char.ToLowerInvariant(c)) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		public static bool IsMixedLanguage(string text, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(text) || text.Length < 8)
			{
				return false;
			}
			bool flag = HasResidualForeignScript(text, targetLanguage);
			bool flag2 = HasResidualEnglish(text, targetLanguage);
			if (flag && flag2)
			{
				return true;
			}
			int num = 0;
			int num2 = 0;
			foreach (char c in text)
			{
				if (char.IsLetter(c))
				{
					int num3 = c;
					if (num3 < 384)
					{
						num++;
					}
					else
					{
						num2++;
					}
				}
			}
			if (num2 >= 2 && num >= 8)
			{
				return true;
			}
			if (flag && num >= 6)
			{
				return true;
			}
			if (string.Equals(LanguageCodeMapper.Canonical(targetLanguage), "Czech", StringComparison.OrdinalIgnoreCase) && HasCzechSignal(text) && (flag2 || HasLeftoverLatin(text)))
			{
				return true;
			}
			return false;
		}

		private static bool HasCzechSignal(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = text.ToLowerInvariant();
			if (ContainsAny(text2, "ěščřžůďťňáéíóúý"))
			{
				return true;
			}
			if (CzechWordShape.IsMatch(text2))
			{
				return true;
			}
			int num = 0;
			MatchCollection matchCollection = CzechWords.Matches(text2);
			foreach (Match item in matchCollection)
			{
				if (item.Success && item.Value.Length >= 3)
				{
					return true;
				}
				if (item.Success && item.Value.Length == 2 && ++num >= 2)
				{
					return true;
				}
			}
			return false;
		}

		public static bool HasResidualForeignScript(string text, string targetLanguage)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			string targetLanguage2 = LanguageCodeMapper.Canonical(targetLanguage);
			for (int i = 0; i < text.Length; i++)
			{
				if (IsForeignScriptChar(text[i], targetLanguage2))
				{
					return true;
				}
			}
			return false;
		}

		public static bool IsForeignScriptChar(char c, string targetLanguage)
		{
			if (!char.IsLetter(c))
			{
				return false;
			}
			if (c <= 'ɏ' || (c >= 'Ḁ' && c <= 'ỿ') || (c >= 'Ⱡ' && c <= 'Ɀ') || (c >= '\ua720' && c <= 'ꟿ'))
			{
				return false;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage ?? "");
			if (ScriptMatchesTarget(c, target))
			{
				return false;
			}
			return true;
		}

		private static bool ScriptMatchesTarget(int n, string target)
		{
			bool flag = (n >= 44032 && n <= 55215) || (n >= 4352 && n <= 4607);
			bool flag2 = n >= 12352 && n <= 12543;
			bool flag3 = (n >= 13312 && n <= 40959) || (n >= 63744 && n <= 64255);
			bool flag4 = n >= 1024 && n <= 1279;
			bool flag5 = (n >= 1536 && n <= 1791) || (n >= 1872 && n <= 1919) || (n >= 64336 && n <= 65023);
			bool flag6 = n >= 880 && n <= 1023;
			bool flag7 = n >= 1424 && n <= 1535;
			bool flag8 = n >= 3584 && n <= 3711;
			bool flag9 = n >= 2304 && n <= 2431;
			if (string.IsNullOrEmpty(target))
			{
				return false;
			}
			if (flag && target.Equals("Korean", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if ((flag2 || flag3) && target.Equals("Japanese", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (flag3 && target.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (flag4 && (target.Equals("Russian", StringComparison.OrdinalIgnoreCase) || target.Equals("Ukrainian", StringComparison.OrdinalIgnoreCase) || target.Equals("Bulgarian", StringComparison.OrdinalIgnoreCase) || target.Equals("Serbian", StringComparison.OrdinalIgnoreCase) || target.Equals("Macedonian", StringComparison.OrdinalIgnoreCase) || target.Equals("Belarusian", StringComparison.OrdinalIgnoreCase)))
			{
				return true;
			}
			if (flag5 && target.Equals("Arabic", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (flag6 && target.Equals("Greek", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (flag7 && target.Equals("Hebrew", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (flag8 && target.Equals("Thai", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (flag9 && (target.Equals("Hindi", StringComparison.OrdinalIgnoreCase) || target.Equals("Sanskrit", StringComparison.OrdinalIgnoreCase)))
			{
				return true;
			}
			return false;
		}

		static LanguageDetector()
		{
			CzechWordShape = new Regex("(?:ní|ně|tí|cí|ští|ská|ské|ský|ová|ové|ého|ému|ými|ách|ami|emi|ou)\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
			CzechWords = new Regex("\\b(?:na|v|ve|do|pro|pod|nad|bez|se|je|má|jsou|který|které|léky|léč|osoba|pocit|obsah|obsahuje|snižuje|zvyšuje|pomáhá|hojí|rány|vlivem|podat|podá|vyrobit|vyrob|výroba|sati|sniž|zvyš|může|muzes|můžeš|mít|mit|dát|dat|udělat|udelat|zdraví|zdravi|nemoc|nemoci|člověk|clovek|potřebuje|potrebuje|muze|máš|mas|bude|budou|chce|chtít|chtit|potom|protože|protoze)\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
			CommonLatinWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"a", "an", "the", "of", "to", "and", "or", "in", "on", "for",
				"with", "from", "make", "craft", "build", "use", "hello", "hi", "please", "item",
				"items", "steel", "wood", "cloth", "wear", "equip", "eat", "drink", "sleep"
			};
			EnglishResiduals = new string[135]
			{
				"the", "and", "with", "from", "that", "this", "have", "make", "craft", "build",
				"hoodie", "shorts", "jeans", "overalls", "tunic", "blouse", "jumpsuit", "tobacco", "leaf", "leaves",
				"smokeleaf", "leather", "wool", "cotton", "rice", "corn", "hay", "berries", "berry", "meat",
				"mushroom", "mushrooms", "wheat", "hops", "chemfuel", "plasteel", "uranium", "grilling", "pottery", "carvings",
				"storage", "research", "terminal", "cannot", "seduce", "converse", "socialize", "recruiting", "conversation", "industrial",
				"desired", "altar", "rector", "effect", "traveling", "description", "equipment", "weapons", "armor", "gloves",
				"jacket", "finished", "started", "recipe", "gizmo", "colonist", "worked", "hauled", "constructed", "cleaned",
				"sowed", "harvested", "attack", "draft", "cancel", "prioritize", "uninstall", "deconstruct", "forbid", "allow",
				"claim", "toggle", "equip", "consume", "install", "hunting", "basics", "weaponry", "clothing", "advanced",
				"growing", "stockpile", "smooth", "remove", "repair", "deliver", "slaughter", "training", "medical", "cooking",
				"mining", "cutting", "harvest", "schedule", "asleep", "marked", "wild", "parquet", "herringbone", "double",
				"rape", "sleep", "plan", "wood", "stone", "steel", "survivor", "officer", "psychic", "weapon",
				"plant", "animal", "medicine", "component", "silver", "quest", "faction", "level", "skill", "meal",
				"power", "stuff", "colonists", "basket", "baskets"
			};
			EnglishResidualSet = new HashSet<string>(EnglishResiduals, StringComparer.Ordinal);
		}

		public static bool HasResidualEnglish(string text, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = LanguageCodeMapper.Canonical(targetLanguage);
			if (string.Equals(text2, "English", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text3 = text.ToLowerInvariant();
			bool flag = EnglishHeuristicsApply(text2);
			int num = 0;
			int num2 = -1;
			for (int i = 0; i <= text3.Length; i++)
			{
				if (i < text3.Length && char.IsLetter(text3[i]))
				{
					if (num2 < 0)
					{
						num2 = i;
					}
					continue;
				}
				if (num2 >= 0)
				{
					string text4 = text3.Substring(num2, i - num2);
					if (EnglishResidualSet.Contains(text4))
					{
						num++;
						bool num3;
						if (!flag)
						{
							num3 = num >= 3;
						}
						else
						{
							if (text4.Length >= 5)
							{
								goto IL_00e3;
							}
							num3 = num >= 2;
						}
						if (num3)
						{
							goto IL_00e3;
						}
					}
				}
				num2 = -1;
				continue;
				IL_00e3:
				return true;
			}
			return false;
		}

		public static bool HasLeftoverLatin(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			if (!EnglishHeuristicsApply(UniversalRimTranslatorSettings.PlayerTargetLanguage()))
			{
				return false;
			}
			int num = -1;
			for (int i = 0; i <= text.Length; i++)
			{
				if (i < text.Length && char.IsLetter(text[i]))
				{
					if (num < 0)
					{
						num = i;
					}
					continue;
				}
				if (num >= 0 && i - num >= 4)
				{
					bool flag = true;
					for (int j = num; j < i; j++)
					{
						if (text[j] > '\u007f')
						{
							flag = false;
							break;
						}
					}
					if (flag)
					{
						string word = text.Substring(num, i - num);
						if (IsEnglishContentWord(word))
						{
							return true;
						}
					}
				}
				num = -1;
			}
			return false;
		}

		private static bool IsEnglishContentWord(string word)
		{
			if (string.IsNullOrEmpty(word) || word.Length < 4)
			{
				return false;
			}
			string text = word.ToLowerInvariant();
			if (CzechWords.IsMatch(text))
			{
				return false;
			}
			for (int i = 0; i < EnglishResiduals.Length; i++)
			{
				if (string.Equals(text, EnglishResiduals[i], StringComparison.Ordinal))
				{
					return true;
				}
			}
			return text.EndsWith("ing", StringComparison.Ordinal) || text.EndsWith("tion", StringComparison.Ordinal) || text.EndsWith("sion", StringComparison.Ordinal) || text.EndsWith("ment", StringComparison.Ordinal) || text.EndsWith("ness", StringComparison.Ordinal) || text.EndsWith("ous", StringComparison.Ordinal);
		}

		public static string RepairMarkup(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text ?? "";
			}
			return Regex.Replace(text, "<(/?)\\s*barv[a-zA-ZáéěíóúůýčďňřšťžÁÉĚÍÓÚŮÝČĎŇŘŠŤŽ]*", "<$1color", RegexOptions.IgnoreCase);
		}

		public static bool HasBrokenMarkup(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			if (text.IndexOf("[#", StringComparison.Ordinal) >= 0)
			{
				return true;
			}
			if (text.IndexOf("[[#", StringComparison.Ordinal) >= 0)
			{
				return true;
			}
			if (Regex.IsMatch(text, "</?\\s*barv", RegexOptions.IgnoreCase))
			{
				return true;
			}
			int count = Regex.Matches(text, "<color\\b", RegexOptions.IgnoreCase).Count;
			int count2 = Regex.Matches(text, "</color>", RegexOptions.IgnoreCase).Count;
			return count != count2;
		}

		public static bool HasEnglishSentence(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			return Regex.IsMatch(text, "\\b(?:the|and|you|they|them|that|with|for|this|have|will|may|their|some|there|because|which|would|could|should|about|from|your|into|when|what|make)\\b(?:\\s+\\w+){6,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		public static bool IsBetterTranslation(string source, string translated, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			string text = (source ?? "").Trim();
			string text2 = translated.Trim();
			if (string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text3 = LanguageCodeMapper.Canonical(targetLanguage ?? UniversalRimTranslatorSettings.PlayerTargetLanguage());
			if (MostlyForeignScript(text2, text3))
			{
				return false;
			}
			int num = CountForeignScript(text, text3);
			int num2 = CountForeignScript(text2, text3);
			if (num2 > num && num2 > 2)
			{
				return false;
			}
			int num3 = CountLeftoverLatin(text);
			int num4 = CountLeftoverLatin(text2);
			if (num4 > num3 + 2)
			{
				return false;
			}
			string a = LanguageCodeMapper.Canonical(text3 ?? UniversalRimTranslatorSettings.PlayerTargetLanguage());
			if (!string.Equals(a, "English", StringComparison.OrdinalIgnoreCase) && HasEnglishSentence(text2))
			{
				return false;
			}
			return true;
		}

		private static int CountForeignScript(string text, string target)
		{
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}
			int num = 0;
			for (int i = 0; i < text.Length; i++)
			{
				if (IsForeignScriptChar(text[i], target))
				{
					num++;
				}
			}
			return num;
		}

		private static int CountLeftoverLatin(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}
			int num = 0;
			int num2 = -1;
			for (int i = 0; i <= text.Length; i++)
			{
				if (i < text.Length && char.IsLetter(text[i]))
				{
					if (num2 < 0)
					{
						num2 = i;
					}
					continue;
				}
				if (num2 >= 0 && i - num2 >= 4)
				{
					bool flag = true;
					for (int j = num2; j < i; j++)
					{
						if (text[j] > '\u007f')
						{
							flag = false;
							break;
						}
					}
					if (flag && IsEnglishContentWord(text.Substring(num2, i - num2)))
					{
						num++;
					}
				}
				num2 = -1;
			}
			return num;
		}

		private static bool MostlyForeignScript(string text, string target)
		{
			int num = 0;
			int num2 = 0;
			for (int i = 0; i < (text ?? "").Length; i++)
			{
				char c = text[i];
				if (char.IsLetter(c))
				{
					num++;
					if (IsForeignScriptChar(c, target))
					{
						num2++;
					}
				}
			}
			return num >= 4 && num2 * 2 >= num;
		}

		public static bool IsTranslationComplete(string source, string translated, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			string text = (source ?? "").Trim();
			string text2 = RepairMarkup(translated).Trim();
			string text3 = LanguageCodeMapper.Canonical(targetLanguage ?? UniversalRimTranslatorSettings.PlayerTargetLanguage());
			if (text2.Length == 0 || HasBrokenMarkup(text2))
			{
				return false;
			}
			if (text.Length > 0 && string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (IsHalfTranslated(text2, text3))
			{
				return false;
			}
			if (HasResidualForeignScript(text2, text3))
			{
				return false;
			}
			if (!string.Equals(text3, "English", StringComparison.OrdinalIgnoreCase) && HasEnglishSentence(text2))
			{
				return false;
			}
			if (!string.Equals(text3, "English", StringComparison.OrdinalIgnoreCase) && LooksMostlySameContentWords(text, text2))
			{
				return false;
			}
			if (!string.Equals(text3, "English", StringComparison.OrdinalIgnoreCase) && AllContentWordsKept(text, text2))
			{
				return false;
			}
			if (!TextHasTargetScript(text2, text3) && CountLetters(text2) >= 3)
			{
				return false;
			}
			string detected = DetectEmbeddedSourceLanguage(text2);
			if (DetectedConflictsWithTarget(text2, detected, text3))
			{
				return false;
			}
			return true;
		}

		private static int CountLetters(string text)
		{
			int num = 0;
			for (int i = 0; i < (text ?? "").Length; i++)
			{
				if (char.IsLetter(text[i]))
				{
					num++;
				}
			}
			return num;
		}

		private static bool AllContentWordsKept(string source, string translated)
		{
			if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			string[] array = Regex.Split(source.ToLowerInvariant(), "[^\\p{L}\\p{N}]+");
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < array.Length; i++)
			{
				if (array[i].Length >= 4)
				{
					hashSet.Add(array[i]);
				}
			}
			if (hashSet.Count < 2 || hashSet.Count > 3)
			{
				return false;
			}
			string[] array2 = Regex.Split(translated.ToLowerInvariant(), "[^\\p{L}\\p{N}]+");
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.Ordinal);
			for (int j = 0; j < array2.Length; j++)
			{
				if (hashSet.Contains(array2[j]))
				{
					hashSet2.Add(array2[j]);
				}
			}
			return hashSet2.Count == hashSet.Count;
		}

		private static bool LooksMostlySameContentWords(string source, string translated)
		{
			if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			string[] array = Regex.Split(source.ToLowerInvariant(), "[^\\p{L}\\p{N}]+");
			string[] array2 = Regex.Split(translated.ToLowerInvariant(), "[^\\p{L}\\p{N}]+");
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string text in array)
			{
				if (text.Length >= 4)
				{
					hashSet.Add(text);
				}
			}
			if (hashSet.Count < 4)
			{
				return false;
			}
			int num = 0;
			foreach (string text2 in array2)
			{
				if (text2.Length >= 4 && hashSet.Contains(text2))
				{
					num++;
				}
			}
			return num >= Math.Max(3, (hashSet.Count + 1) / 2);
		}

		public static bool IsHalfTranslated(string text, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
			{
				return false;
			}
			string text2 = LanguageCodeMapper.Canonical(targetLanguage ?? UniversalRimTranslatorSettings.PlayerTargetLanguage());
			if (string.Equals(text2, "English", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (string.Equals(text2, "Czech", StringComparison.OrdinalIgnoreCase) && HasCzechSignal(text) && (HasResidualEnglish(text, text2) || HasLeftoverLatin(text)))
			{
				return true;
			}
			if (HasResidualForeignScript(text, text2))
			{
				return true;
			}
			if (LooksBadlyTranslated("", text, text2))
			{
				return true;
			}
			bool flag = string.Equals(text2, "Czech", StringComparison.OrdinalIgnoreCase);
			if (flag && LooksLikeCzech(text) && (HasResidualEnglish(text, text2) || HasLeftoverLatin(text)))
			{
				return true;
			}
			if (!flag)
			{
				return HasResidualEnglish(text, text2);
			}
			if (LooksLikeCzech(text) || LooksLikeProperNameOrCode(text))
			{
				return false;
			}
			int num = 0;
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] == ' ')
				{
					num++;
				}
			}
			if (num <= 4 && Regex.IsMatch(text, "^[A-Za-z][A-Za-z' ]{2,60}$"))
			{
				return true;
			}
			return false;
		}

		public static bool LooksBadlyTranslated(string source, string translation, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(translation))
			{
				return true;
			}
			string text = source ?? "";
			string text2 = translation.Trim();
			if (LooksLikeProperNameOrCode(text2) && text2.Length <= 24)
			{
				return false;
			}
			if (text2.IndexOf("[[#", StringComparison.Ordinal) >= 0)
			{
				return true;
			}
			string text3 = LanguageCodeMapper.Canonical(targetLanguage);
			if (HasResidualForeignScript(text2, text3))
			{
				return true;
			}
			string detected = DetectEmbeddedSourceLanguage(text2);
			if (DetectedConflictsWithTarget(text2, detected, text3))
			{
				return true;
			}
			if (string.Equals(text3, "Czech", StringComparison.OrdinalIgnoreCase) || string.Equals(text3, "Čeština", StringComparison.OrdinalIgnoreCase))
			{
				bool flag = Regex.IsMatch(text2, "\\b(Basic|Advanced|Weaponry|Clothing|Plant|Research|Hide Auto|Carve|Make |Selling|Crossbow|Roll)\\b");
				bool flag2 = LooksLikeCzech(text2) || Regex.IsMatch(text2, "[áéíóúýčďěňřšťž]");
				if (flag && flag2)
				{
					return true;
				}
				if (flag && !flag2 && text2.Split(new char[1] { ' ' }).Length <= 6)
				{
					return true;
				}
			}
			if (string.Equals(text2, text.Trim(), StringComparison.Ordinal))
			{
				string text4 = DetectEmbeddedSourceLanguage(text);
				if (string.IsNullOrEmpty(text4))
				{
					text4 = DetectScriptLanguage(text);
				}
				return !string.IsNullOrEmpty(text4) && !string.Equals(text4, text3, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}
	public static class LanguageInjection
	{
		public static string Cleanup(string text)
		{
			return string.IsNullOrEmpty(text) ? text : text.Trim();
		}

		public static bool InjectOne(CachedTranslation entry)
		{
			if (entry == null || string.IsNullOrWhiteSpace(entry.Translation))
			{
				return false;
			}
			if (!UrtMainThread.IsMain)
			{
				// b114: LanguageDatabase is only changed on the main thread
				UrtMainThread.Post(delegate
				{
					InjectOne(entry);
				});
				return false;
			}
			LoadedLanguage activeLanguage = LanguageDatabase.activeLanguage;
			if (activeLanguage == null || activeLanguage.keyedReplacements == null)
			{
				return false;
			}
			// b113: DefInjected entries (Steel.label …) are applied to Defs, not injected as Keyed strings
			if (string.Equals(entry.Type, "DefInjected", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text = entry.Key ?? "";
			if (!LooksLikeKeyedName(text) || text.StartsWith("live.", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text2 = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			if (!string.Equals(LanguageCodeMapper.Canonical(entry.TargetLanguage), text2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text3 = Cleanup(entry.Translation);
			if (string.IsNullOrWhiteSpace(text3))
			{
				return false;
			}
			if (LanguageDetector.IsHalfTranslated(text3, UniversalRimTranslatorSettings.PlayerTargetLanguage()))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(entry.SourceText) && string.Equals(text3.Trim(), entry.SourceText.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			try
			{
				if (activeLanguage.keyedReplacements.TryGetValue(text, out var value) && value != null && !string.IsNullOrWhiteSpace(value.value))
				{
					string text4 = value.value.Trim();
					if ((string.IsNullOrEmpty(entry.SourceText) || !string.Equals(text4, entry.SourceText.Trim(), StringComparison.OrdinalIgnoreCase)) && !LanguageDetector.HasResidualEnglish(text4, text2))
					{
						return false;
					}
				}
				activeLanguage.keyedReplacements[text] = new LoadedLanguage.KeyedReplacement
				{
					key = text,
					value = text3,
					isPlaceholder = false,
					fileSource = "UniversalRimTranslator",
					fileSourceFullPath = "UniversalRimTranslator",
					fileSourceLine = 1
				};
				UrtCzechXml.RememberKeyed(text, text3);
				return true;
			}
			catch
			{
				return false;
			}
		}

		public static void Inject(TranslationCache cache)
		{
			if (cache == null)
			{
				return;
			}
			if (!UrtMainThread.IsMain)
			{
				UrtMainThread.Post(delegate
				{
					Inject(cache);
				});
				return;
			}
			int num = 0;
			foreach (CachedTranslation item in cache.GetAll())
			{
				if (InjectOne(item))
				{
					num++;
				}
			}
			if (num > 0)
			{
				MLog.Info("[Universal RimTranslator] Injected " + num + " keyed strings into active language.");
			}
			UrtCzechXml.Flush();
		}

		public static int HarvestMissing(int cap)
		{
			if (cap < 1)
			{
				cap = 1;
			}
			if (!UrtMainThread.IsMain)
			{
				// b114: enumerating keyedReplacements while the main thread changes it throws
				int capCopy = cap;
				UrtMainThread.Post(delegate
				{
					HarvestMissing(capCopy);
				});
				return 0;
			}
			int num = 0;
			string target = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			LoadedLanguage activeLanguage = LanguageDatabase.activeLanguage;
			if (activeLanguage != null && activeLanguage.keyedReplacements != null)
			{
				foreach (KeyValuePair<string, LoadedLanguage.KeyedReplacement> keyedReplacement in activeLanguage.keyedReplacements)
				{
					if (num >= cap)
					{
						break;
					}
					string text = ((keyedReplacement.Value == null) ? null : keyedReplacement.Value.value);
					if (NeedsGameTranslation(text, target) && RuntimeLiveQueue.Enqueue(text))
					{
						num++;
					}
				}
			}
			num += HarvestDefText(DefDatabase<ThingDef>.AllDefsListForReading, cap - num, target, (ThingDef d) => d.label, (ThingDef d) => d.description);
			num += HarvestDefText(DefDatabase<ResearchProjectDef>.AllDefsListForReading, cap - num, target, (ResearchProjectDef d) => d.label, (ResearchProjectDef d) => d.description);
			num += HarvestDefText(DefDatabase<QuestScriptDef>.AllDefsListForReading, cap - num, target, (QuestScriptDef d) => d.label, (QuestScriptDef d) => d.description);
			MLog.Info("[URT] Game-missing queued " + num + " (keyed, things, research, quests).");
			return num;
		}

		private static int HarvestDefText<T>(IEnumerable<T> defs, int room, string target, Func<T, string> label, Func<T, string> description) where T : Def
		{
			if (room <= 0 || defs == null)
			{
				return 0;
			}
			int num = 0;
			foreach (T def in defs)
			{
				if (num >= room)
				{
					break;
				}
				if (def != null)
				{
					if (NeedsGameTranslation(label(def), target) && RuntimeLiveQueue.Enqueue(label(def)))
					{
						num++;
					}
					if (num >= room)
					{
						break;
					}
					if (NeedsGameTranslation(description(def), target) && RuntimeLiveQueue.Enqueue(description(def)))
					{
						num++;
					}
				}
			}
			return num;
		}

		private static bool NeedsGameTranslation(string text, string target)
		{
			if (string.IsNullOrWhiteSpace(text) || text.Length < 4 || text.Length > 2000)
			{
				return false;
			}
			if (LanguageDetector.IsForeignScriptChar(text[0], target) || HasForeign(text))
			{
				return true;
			}
			if (LanguageDetector.IsHalfTranslated(text, target))
			{
				return true;
			}
			return LanguageDetector.HasResidualEnglish(text, target);
		}

		private static bool HasForeign(string text)
		{
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] > 'ɏ' && char.IsLetter(text[i]))
				{
					return true;
				}
			}
			return false;
		}

		public static int ImportMissingKeyed(TranslationDatabase database)
		{
			if (database == null)
			{
				return 0;
			}
			LoadedLanguage activeLanguage = LanguageDatabase.activeLanguage;
			LoadedLanguage defaultLanguage = LanguageDatabase.defaultLanguage;
			if (activeLanguage == null || defaultLanguage == null || activeLanguage == defaultLanguage)
			{
				return 0;
			}
			try
			{
				defaultLanguage.LoadData();
			}
			catch
			{
			}
			if (defaultLanguage.keyedReplacements == null)
			{
				return 0;
			}
			int num = 0;
			foreach (KeyValuePair<string, LoadedLanguage.KeyedReplacement> keyedReplacement in defaultLanguage.keyedReplacements)
			{
				string key = keyedReplacement.Key;
				if (string.IsNullOrWhiteSpace(key) || (activeLanguage.keyedReplacements != null && activeLanguage.keyedReplacements.ContainsKey(key)))
				{
					continue;
				}
				LoadedLanguage.KeyedReplacement value = keyedReplacement.Value;
				string text = value?.value;
				if (string.IsNullOrWhiteSpace(text) || (text.IndexOf('.') >= 0 && text.IndexOf(' ') < 0 && text.Length < 48))
				{
					continue;
				}
				string modPackageId = "";
				string text2 = "Keyed (EN missing in target)";
				try
				{
					if (value != null && !string.IsNullOrEmpty(value.fileSourceFullPath))
					{
						foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
						{
							if (item == null || string.IsNullOrEmpty(item.RootDir) || value.fileSourceFullPath.IndexOf(item.RootDir, StringComparison.OrdinalIgnoreCase) < 0)
							{
								continue;
							}
							modPackageId = item.PackageId ?? "";
							text2 = item.Name ?? text2;
							break;
						}
					}
				}
				catch (Exception ex)
				{
					MLog.Warn("[URT] Language injection pack lookup failed: " + ex.Message);
				}
				database.Add(new TranslationEntry
				{
					ModName = text2,
					ModPackageId = modPackageId,
					Language = "English",
					Key = key,
					Text = text,
					FilePath = ((value != null) ? value.fileSourceFullPath : ""),
					Type = "Keyed",
					DefType = ""
				});
				num++;
			}
			MLog.Info("[Universal RimTranslator] Missing keyed imported: " + num + ".");
			return num;
		}

		private static bool LooksLikeKeyedName(string key)
		{
			if (string.IsNullOrWhiteSpace(key) || key.Length < 4)
			{
				return false;
			}
			if (key.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (key.IndexOf(' ') >= 0)
			{
				return false;
			}
			bool result = false;
			foreach (char c in key)
			{
				if (char.IsLetter(c))
				{
					result = true;
				}
				else if (c != '_' && c != '.' && !char.IsDigit(c))
				{
					return false;
				}
			}
			return result;
		}
	}
	public static class LanguageNormalizer
	{
		private static readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "English", "English" },
			{ "Czech", "Czech" },
			{ "ChineseSimplified", "ChineseSimplified" },
			{ "ChineseSimplified (简体中文)", "ChineseSimplified" },
			{ "ChineseTraditional", "ChineseTraditional" },
			{ "ChineseTraditional (繁體中文)", "ChineseTraditional" },
			{ "Japanese", "Japanese" },
			{ "Japanese (日本語)", "Japanese" },
			{ "Korean", "Korean" },
			{ "Korean (한국어)", "Korean" },
			{ "KoreanFastChannel", "Korean" },
			{ "Vietnamese", "Vietnamese" },
			{ "German", "German" },
			{ "German (Deutsch)", "German" },
			{ "French", "French" },
			{ "French (Français)", "French" },
			{ "Spanish", "Spanish" },
			{ "Spanish (Español Castellano)", "Spanish" },
			{ "Polish", "Polish" },
			{ "Polish (Polski)", "Polish" },
			{ "Russian", "Russian" },
			{ "Russian (Русский)", "Russian" },
			{ "Portuguese", "Portuguese" },
			{ "PortugueseBrazilian", "PortugueseBrazilian" },
			{ "PortugueseBrazilian (Português Brasileiro)", "PortugueseBrazilian" },
			{ "Ukranian", "Ukrainian" },
			{ "Ukrainian (Українська)", "Ukrainian" },
			{ "SpanishLatin", "SpanishLatin" },
			{ "SpanishLatin (Español(Latinoamérica))", "SpanishLatin" },
			{ "Catalan (Català)", "Catalan" },
			{ "Danish (Dansk)", "Danish" },
			{ "Dutch (Nederlands)", "Dutch" },
			{ "Estonian (Eesti)", "Estonian" },
			{ "Finnish (Suomi)", "Finnish" },
			{ "Greek (Ελληνικά)", "Greek" },
			{ "Hungarian (Magyar)", "Hungarian" },
			{ "Italian (Italiano)", "Italian" },
			{ "Norwegian (Norsk Bokmål)", "Norwegian" },
			{ "Romanian (Română)", "Romanian" },
			{ "Slovak (Slovenčina)", "Slovak" },
			{ "Swedish (Svenska)", "Swedish" },
			{ "Turkish (Türkçe)", "Turkish" },
			{ "Vietnamese (Tiếng Việt)", "Vietnamese" },
			{ "Czech (Čeština)", "Czech" }
		};

		public static string Normalize(string language)
		{
			if (string.IsNullOrEmpty(language))
			{
				return "";
			}
			if (aliases.TryGetValue(language.Trim(), out var value))
			{
				return value;
			}
			return language.Trim();
		}
	}
	public sealed class ScanSummary
	{
		public int ModsScanned;

		public int ModsWithLanguages;

		public int ModsWithTranslatableText;

		public int LanguageFolders;

		public int FilesScanned;

		public int EntriesExtracted;

		public int EntriesSkippedCached;

		public int KeyedFiles;

		public int DefInjectedFiles;

		public int SourceDefFiles;

		public int SourceDefsFound;

		public int SourceFieldsExtracted;

		public int RuntimeRecipeDefsFound;

		public int RuntimeRecipeFieldsExtracted;

		public int RuntimeDefTypesScanned;

		public int RuntimeDefFieldsExtracted;

		public int CoreFilesScanned;

		public int CoreEntriesExtracted;

		public int Errors;

		public List<string> Languages = new List<string>();

		internal HashSet<string> TranslatableModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}
	public static class ModScanner
	{
		public sealed class LanguageStats
		{
			public readonly Dictionary<string, int> Defs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			public readonly Dictionary<string, int> Packs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		}

		private static TranslationCache skipCache;

		private static string skipTarget;

		private static HashSet<string> skipSources;

		private static int skippedCached;

		private static bool limitWarned;

		private static string lastModForCap;

		private static int currentModNewEntries;

		private static int modsHitCap;

		public static Dictionary<string, LanguageStats> LastLanguageStats = new Dictionary<string, LanguageStats>(StringComparer.OrdinalIgnoreCase);

		public static int EntryLimit()
		{
			return 1000000;
		}

		private static int PerModEntryLimit()
		{
			return 200000;
		}

		private static bool HitEntryCap(ScanSummary summary, string modName, string packageId)
		{
			if (summary == null)
			{
				return false;
			}
			string text = packageId ?? modName ?? "";
			if (!string.Equals(lastModForCap, text, StringComparison.OrdinalIgnoreCase))
			{
				lastModForCap = text;
				currentModNewEntries = 0;
			}
			if (summary.EntriesExtracted >= EntryLimit())
			{
				if (!limitWarned)
				{
					limitWarned = true;
					MLog.Warn("[URT] Global scan limit reached. Further new entries skipped. Last mod: " + modName);
				}
				return true;
			}
			if (currentModNewEntries >= PerModEntryLimit())
			{
				if (modsHitCap < 40)
				{
					modsHitCap++;
					MLog.Warn("[URT] Per-mod scan limit reached for: " + (modName ?? text));
				}
				return true;
			}
			return false;
		}

		public static void BeginCacheSkip(TranslationCache cache, string targetLanguage)
		{
			skipCache = cache;
			skipTarget = LanguageCodeMapper.Canonical(targetLanguage);
			skipSources = cache?.BuildTranslatedSourceSet(skipTarget);
			skippedCached = 0;
			limitWarned = false;
			lastModForCap = null;
			currentModNewEntries = 0;
			modsHitCap = 0;
		}

		public static void EndCacheSkip()
		{
			skipCache = null;
			skipTarget = null;
			skipSources = null;
		}

		private static bool ShouldSkipCached(string packageId, string type, string defType, string key, string text)
		{
			if (skipCache == null || string.IsNullOrEmpty(skipTarget))
			{
				return false;
			}
			if (skipCache.IsReallyCached(packageId, type, defType, key, text, skipTarget))
			{
				return true;
			}
			if (LanguageDetector.LooksLikeTargetText(text, skipTarget) && !LanguageDetector.IsHalfTranslated(text, skipTarget) && !LanguageDetector.HasLeftoverLatin(text))
			{
				return true;
			}
			return false;
		}

		public static ScanSummary Scan(TranslationDatabase database, bool includeUniversalRimTranslator = false)
		{
			if (database == null)
			{
				throw new ArgumentNullException("database");
			}
			ScanSummary scanSummary = new ScanSummary();
			string translatorRoot = GetTranslatorRoot();
			lock (LastLanguageStats)
			{
				LastLanguageStats = new Dictionary<string, LanguageStats>(StringComparer.OrdinalIgnoreCase);
			}
			MLog.Info("[Universal RimTranslator] Scan started. b67-key-only Wide=" + UniversalRimTranslatorSettings.UseWideCoverage() + " EntryLimit=" + EntryLimit() + " PerModLimit=" + PerModEntryLimit());
			List<ModContentPack> runningModsListForReading = LoadedModManager.RunningModsListForReading;
			int num = runningModsListForReading?.Count ?? 0;
			int num2 = 0;
			foreach (ModContentPack item in runningModsListForReading)
			{
				num2++;
				if (item == null)
				{
					continue;
				}
				if (num2 % 15 == 0)
				{
					MLog.Info("[URT] Scan progress " + num2 + "/" + num + " entries=" + scanSummary.EntriesExtracted + " skippedCache=" + scanSummary.EntriesSkippedCached);
				}
				string rootDir = item.RootDir;
				if (!includeUniversalRimTranslator && !string.IsNullOrEmpty(translatorRoot) && !string.IsNullOrEmpty(rootDir) && PathsEqual(rootDir, translatorRoot))
				{
					continue;
				}
				scanSummary.ModsScanned++;
				try
				{
					if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
					{
						continue;
					}
					foreach (string item2 in LoadFolders(item))
					{
						ScanLanguages(item, item2, database, scanSummary);
					}
				}
				catch (Exception ex)
				{
					MLog.Warn("[Universal RimTranslator] Could not scan " + (item.Name ?? "<unknown>") + ": " + ex.Message);
				}
			}
			if (!ScanDefInjections(database, scanSummary, translatorRoot, includeUniversalRimTranslator))
			{
				MLog.Warn("[URT] b99 Def scan through the game failed; falling back to reading Defs XML.");
				foreach (ModContentPack item3 in runningModsListForReading)
				{
					if (item3 == null || string.IsNullOrEmpty(item3.RootDir) || (!includeUniversalRimTranslator && !string.IsNullOrEmpty(translatorRoot) && PathsEqual(item3.RootDir, translatorRoot)))
					{
						continue;
					}
					try
					{
						foreach (string item4 in LoadFolders(item3))
						{
							ScanSourceDefs(item3, item4, database, scanSummary);
						}
					}
					catch
					{
					}
				}
				ScanLoadedDefDatabases(database, scanSummary);
			}
			ScanCoreFallback(database, scanSummary);
			scanSummary.Languages.Sort(StringComparer.OrdinalIgnoreCase);
			MLog.Info("[Universal RimTranslator] Extraction complete. Mods=" + scanSummary.ModsScanned + ", Files=" + scanSummary.FilesScanned + ", Entries=" + scanSummary.EntriesExtracted + ", SkippedCached=" + scanSummary.EntriesSkippedCached + ", SourceDefs=" + scanSummary.SourceDefsFound + ", SourceFields=" + scanSummary.SourceFieldsExtracted + ", Languages=" + string.Join(", ", scanSummary.Languages));
			return scanSummary;
		}

		internal static List<string> LoadFolders(ModContentPack content)
		{
			List<string> list = new List<string>();
			if (content == null || string.IsNullOrEmpty(content.RootDir))
			{
				return list;
			}
			try
			{
				List<string> foldersToLoadDescendingOrder = content.foldersToLoadDescendingOrder;
				if (foldersToLoadDescendingOrder != null)
				{
					for (int i = 0; i < foldersToLoadDescendingOrder.Count; i++)
					{
						string text = foldersToLoadDescendingOrder[i];
						if (!string.IsNullOrEmpty(text))
						{
							if (!Path.IsPathRooted(text))
							{
								text = Path.Combine(content.RootDir, text);
							}
							if (Directory.Exists(text) && !ContainsPath(list, text))
							{
								list.Add(text);
							}
						}
					}
				}
			}
			catch
			{
			}
			if (list.Count == 0 && Directory.Exists(content.RootDir))
			{
				list.Add(content.RootDir);
			}
			return list;
		}

		private static bool ContainsPath(List<string> list, string path)
		{
			string b = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(Path.GetFullPath(list[i]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), b, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static bool LooksLikeLanguageFolder(string name)
		{
			if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".", StringComparison.Ordinal))
			{
				return false;
			}
			switch (name.Trim().ToLowerInvariant())
			{
			case "keyed":
			case "definjected":
			case "strings":
			case "backstories":
			case "wordinfo":
			case "source":
			case "bin":
			case "obj":
			case "old":
			case "backup":
			case "assemblies":
			case "textures":
			case "sounds":
			case "defs":
			case "patches":
			case "about":
				return false;
			default:
				return true;
			}
		}

		private static void ScanLanguages(ModContentPack content, string root, TranslationDatabase database, ScanSummary summary)
		{
			string path = Path.Combine(root, "Languages");
			if (!Directory.Exists(path))
			{
				return;
			}
			summary.ModsWithLanguages++;
			string[] directories = Directory.GetDirectories(path);
			foreach (string text in directories)
			{
				if (!LooksLikeLanguageFolder(Path.GetFileName(text)))
				{
					continue;
				}
				summary.LanguageFolders++;
				string text2 = LanguageCodeMapper.Canonical(LanguageNormalizer.Normalize(Path.GetFileName(text)));
				if (string.IsNullOrWhiteSpace(text2))
				{
					text2 = Path.GetFileName(text);
				}
				if (!string.IsNullOrWhiteSpace(text2))
				{
					if (!summary.Languages.Contains(text2, StringComparer.OrdinalIgnoreCase))
					{
						summary.Languages.Add(text2);
					}
					ScanType(content, text2, Path.Combine(text, "Keyed"), "Keyed", database, summary);
					ScanType(content, text2, Path.Combine(text, "DefInjected"), "DefInjected", database, summary);
					if (UniversalRimTranslatorSettings.UseWideCoverage())
					{
						ScanType(content, text2, Path.Combine(text, "Strings"), "Keyed", database, summary);
						ScanType(content, text2, Path.Combine(text, "RulePacks"), "Keyed", database, summary);
					}
				}
			}
		}

		private static void ScanType(ModContentPack content, string language, string directory, string type, TranslationDatabase database, ScanSummary summary)
		{
			if (!Directory.Exists(directory))
			{
				return;
			}
			string[] files = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories);
			foreach (string text in files)
			{
				summary.FilesScanned++;
				if (string.Equals(type, "Keyed", StringComparison.OrdinalIgnoreCase))
				{
					summary.KeyedFiles++;
				}
				else
				{
					summary.DefInjectedFiles++;
				}
				try
				{
					XDocument xDocument = XDocument.Load(text, LoadOptions.PreserveWhitespace);
					if (xDocument.Root != null)
					{
						if (string.Equals(type, "Keyed", StringComparison.OrdinalIgnoreCase))
						{
							ExtractKeyed(content, language, text, xDocument.Root, database, summary);
						}
						else
						{
							ExtractDefInjected(content, language, text, xDocument.Root, database, summary);
						}
					}
				}
				catch (Exception)
				{
					summary.Errors++;
				}
			}
		}

		private static void ExtractKeyed(ModContentPack content, string language, string file, XElement root, TranslationDatabase database, ScanSummary summary)
		{
			foreach (XElement item in root.DescendantsAndSelf())
			{
				if (!item.HasElements && !string.IsNullOrWhiteSpace(item.Value))
				{
					string localName = item.Name.LocalName;
					if (!IsTechnicalText(localName, item.Value))
					{
						AddEntry(content, language, file, "Keyed", "", localName, item.Value, database, summary);
					}
				}
			}
		}

		private static void ExtractDefInjected(ModContentPack content, string language, string file, XElement root, TranslationDatabase database, ScanSummary summary)
		{
			if (!string.Equals(root.Name.LocalName, "LanguageData", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			string defTypeFromPath = GetDefTypeFromPath(file);
			if (string.IsNullOrEmpty(defTypeFromPath))
			{
				return;
			}
			foreach (XElement item in root.Descendants())
			{
				if (!item.HasElements && !string.IsNullOrWhiteSpace(item.Value))
				{
					string localName = item.Name.LocalName;
					if (!IsTechnicalText(localName, item.Value))
					{
						AddEntry(content, language, file, "DefInjected", defTypeFromPath, localName, item.Value, database, summary);
					}
				}
			}
		}

		private static void ScanSourceDefs(ModContentPack content, string root, TranslationDatabase database, ScanSummary summary)
		{
			string path = Path.Combine(root, "Defs");
			if (!Directory.Exists(path))
			{
				return;
			}
			if (!summary.Languages.Contains("English", StringComparer.OrdinalIgnoreCase))
			{
				summary.Languages.Add("English");
			}
			string[] files = Directory.GetFiles(path, "*.xml", SearchOption.AllDirectories);
			foreach (string text in files)
			{
				summary.FilesScanned++;
				summary.SourceDefFiles++;
				try
				{
					XDocument xDocument = XDocument.Load(text, LoadOptions.PreserveWhitespace);
					if (xDocument.Root != null)
					{
						ExtractSourceDefs(content, text, xDocument.Root, database, summary);
					}
				}
				catch (Exception ex)
				{
					MLog.Warn("[Universal RimTranslator] Defs XML parse failed: " + text + " | " + ex.Message);
				}
			}
		}

		private static void ExtractSourceDefs(ModContentPack content, string file, XElement root, TranslationDatabase database, ScanSummary summary)
		{
			IEnumerable<XElement> enumerable = root.Elements().Where(HasDefName);
			if (HasDefName(root))
			{
				enumerable = new XElement[1] { root };
			}
			foreach (XElement item in enumerable)
			{
				string directChildValue = GetDirectChildValue(item, "defName");
				if (string.IsNullOrWhiteSpace(directChildValue))
				{
					continue;
				}
				summary.SourceDefsFound++;
				string localName = item.Name.LocalName;
				foreach (XElement item2 in item.Elements())
				{
					if (!item2.HasElements && !string.IsNullOrWhiteSpace(item2.Value))
					{
						string localName2 = item2.Name.LocalName;
						if (IsTranslatableDefField(localName2) && !IsTechnicalText(localName2, item2.Value))
						{
							AddSourceEntry(content, file, localName, directChildValue + "." + localName2, item2.Value, database, summary);
							summary.SourceFieldsExtracted++;
						}
					}
				}
				if (!UniversalRimTranslatorSettings.UseWideCoverage())
				{
					continue;
				}
				foreach (XElement item3 in from x in item.Descendants()
					where !x.HasElements
					select x)
				{
					if (item3.Parent == item || string.IsNullOrWhiteSpace(item3.Value))
					{
						continue;
					}
					string localName3 = item3.Name.LocalName;
					bool flag = IsProseLi(item3);
					if ((IsNestedTextField(localName3) || flag) && (!IsTechnicalText(localName3, item3.Value) || flag))
					{
						string text = BuildRelativePath(item, item3);
						if (!string.IsNullOrEmpty(text))
						{
							AddSourceEntry(content, file, localName, directChildValue + "." + text, item3.Value, database, summary);
							summary.SourceFieldsExtracted++;
						}
					}
				}
			}
		}

		private static void ScanLoadedRecipeDefs(TranslationDatabase database, ScanSummary summary)
		{
			try
			{
				foreach (RecipeDef item in DefDatabase<RecipeDef>.AllDefsListForReading)
				{
					if (item != null)
					{
						ModContentPack modContentPack = item.modContentPack;
						if (modContentPack != null && !string.IsNullOrWhiteSpace(modContentPack.PackageId) && !string.IsNullOrWhiteSpace(item.defName))
						{
							summary.RuntimeRecipeDefsFound++;
							AddRuntimeRecipeField(modContentPack, item, "label", item.label, database, summary);
							AddRuntimeRecipeField(modContentPack, item, "description", item.description, database, summary);
							AddRuntimeRecipeField(modContentPack, item, "jobString", item.jobString, database, summary);
						}
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Universal RimTranslator] Runtime RecipeDef scan failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private static bool ScanDefInjections(TranslationDatabase database, ScanSummary summary, string translatorRoot, bool includeUniversalRimTranslator)
		{
			int added = 0;
			int num = 0;
			int skippedOfficial = 0;
			bool officialTarget = HasOfficialTargetLanguage();
			try
			{
				foreach (Type defType in AllDefTypes())
				{
					num++;
					try
					{
						DefInjectionUtility.ForEachPossibleDefInjection(defType, delegate(string suggestedPath, string normalizedPath, bool isCollection, string currentValue, IEnumerable<string> currentValueCollection, bool translationAllowed, bool fullListTranslationAllowed, FieldInfo fieldInfo, Def def)
						{
							if (translationAllowed && def != null && !def.generated && !(fieldInfo == null))
							{
								ModContentPack modContentPack = def.modContentPack;
								if (modContentPack != null && !string.IsNullOrWhiteSpace(modContentPack.PackageId) && (includeUniversalRimTranslator || string.IsNullOrEmpty(translatorRoot) || !PathsEqual(modContentPack.RootDir ?? "", translatorRoot)))
								{
									if (officialTarget && modContentPack.IsOfficialMod)
									{
										int num2 = skippedOfficial;
										skippedOfficial = num2 + 1;
									}
									else if (!Excluded(fieldInfo))
									{
										string path = modContentPack.RootDir ?? "";
										char directorySeparatorChar = Path.DirectorySeparatorChar;
										string file = Path.Combine(path, "Defs" + directorySeparatorChar + defType.Name);
										if (!isCollection)
										{
											string original;
											string text = (DefTranslationApplier.TryGetOriginal(def.GetType(), normalizedPath, out original) ? original : currentValue);
											if (Wanted(text, fieldInfo, def))
											{
												AddSourceEntry(modContentPack, file, defType.Name, normalizedPath, text, database, summary);
												int num2 = added;
												added = num2 + 1;
											}
										}
										else if (currentValueCollection != null)
										{
											int num3 = 0;
											foreach (string item in currentValueCollection)
											{
												string original2;
												string text2 = (DefTranslationApplier.TryGetOriginal(def.GetType(), normalizedPath + "." + num3, out original2) ? original2 : item);
												if (Wanted(text2, fieldInfo, def) && !IsNameRule(text2, def))
												{
													AddSourceEntry(modContentPack, file, defType.Name, normalizedPath + "." + num3, text2, database, summary);
													int num2 = added;
													added = num2 + 1;
												}
												num3++;
											}
										}
									}
								}
							}
						});
					}
					catch (Exception ex)
					{
						summary.Errors++;
						MLog.Warn("[URT] b99 Def scan skipped " + defType.Name + ": " + ex.GetType().Name + " " + ex.Message);
					}
				}
			}
			catch (Exception ex2)
			{
				MLog.Warn("[URT] b99 Def scan failed: " + ex2.GetType().Name + " " + ex2.Message);
				return false;
			}
			summary.RuntimeDefTypesScanned = num;
			summary.RuntimeDefFieldsExtracted = added;
			MLog.Info("[URT] b99 Def scan through the game: " + added + " texts from " + num + " Def types" + ((skippedOfficial > 0) ? (", " + skippedOfficial + " official (Core/DLC) skipped: the game has the target language") : "") + ".");
			return num > 0;
		}

		private static IEnumerable<Type> AllDefTypes()
		{
			List<Type> list = new List<Type>();
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly assembly in assemblies)
			{
				Type[] array;
				try
				{
					array = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					array = ex.Types.Where((Type t) => t != null).ToArray();
				}
				catch
				{
					continue;
				}
				Type[] array2 = array;
				foreach (Type type in array2)
				{
					if (type != null && !type.IsAbstract && typeof(Def).IsAssignableFrom(type))
					{
						list.Add(type);
					}
				}
			}
			return list;
		}

		internal static bool IsNameRule(string text, Def def)
		{
			string text2 = UrtResultValidator.RulePrefixOf(text);
			if (text2 == null)
			{
				return false;
			}
			string text3 = text2;
			int num = text3.IndexOf('(');
			if (num < 0)
			{
				num = text3.IndexOf("->", StringComparison.Ordinal);
			}
			if (num > 0)
			{
				text3 = text3.Substring(0, num);
			}
			if (text3.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			return def is RulePackDef && def.defName != null && (def.defName.IndexOf("Namer", StringComparison.OrdinalIgnoreCase) >= 0 || def.defName.IndexOf("Names", StringComparison.OrdinalIgnoreCase) >= 0 || def.defName.StartsWith("Name", StringComparison.OrdinalIgnoreCase));
		}

		private static bool Excluded(FieldInfo fi)
		{
			return fi.IsDefined(typeof(NoTranslateAttribute), inherit: true) || fi.IsDefined(typeof(UnsavedAttribute), inherit: true) || fi.IsDefined(typeof(MayTranslateAttribute), inherit: true);
		}

		private static bool Wanted(string text, FieldInfo fi, Def def)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			if (IsTechnicalText(fi.Name, text))
			{
				return false;
			}
			bool flag = false;
			try
			{
				flag = DefInjectionUtility.ShouldCheckMissingInjection(text, fi, def);
			}
			catch
			{
			}
			return flag || IsNestedTextField(fi.Name);
		}

		private static bool HasOfficialTargetLanguage()
		{
			try
			{
				string b = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
				foreach (LoadedLanguage allLoadedLanguage in LanguageDatabase.AllLoadedLanguages)
				{
					if (allLoadedLanguage == null || (!string.Equals(LanguageCodeMapper.Canonical(allLoadedLanguage.folderName), b, StringComparison.OrdinalIgnoreCase) && !string.Equals(LanguageCodeMapper.Canonical(allLoadedLanguage.LegacyFolderName), b, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}
					return true;
				}
			}
			catch
			{
			}
			return false;
		}

		private static void AddRuntimeRecipeField(ModContentPack content, RecipeDef recipe, string field, string value, TranslationDatabase database, ScanSummary summary)
		{
			if (!string.IsNullOrWhiteSpace(value) && !IsTechnicalText(field, value))
			{
				AddSourceEntry(content, "<runtime RecipeDef>", "RecipeDef", recipe.defName + "." + field, value, database, summary);
				summary.RuntimeRecipeFieldsExtracted++;
			}
		}

		private static bool IsRulesStringLeaf(XElement leaf)
		{
			if (leaf == null || !string.Equals(leaf.Name.LocalName, "li", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			XElement parent = leaf.Parent;
			return parent != null && string.Equals(parent.Name.LocalName, "rulesStrings", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsProseLi(XElement leaf)
		{
			if (IsRulesStringLeaf(leaf))
			{
				return true;
			}
			if (leaf == null || !string.Equals(leaf.Name.LocalName, "li", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text = (leaf.Value ?? "").Trim();
			if (text.Length < 24 || text.IndexOf(' ') < 0)
			{
				return false;
			}
			if (text.IndexOf("defName", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf(' ') < 0)
			{
				return false;
			}
			int num = 0;
			for (int i = 0; i < text.Length; i++)
			{
				if (char.IsLetter(text[i]))
				{
					num++;
				}
			}
			return num >= 12;
		}

		private static void ScanLoadedDefDatabases(TranslationDatabase database, ScanSummary summary)
		{
			HashSet<Type> hashSet = new HashSet<Type>();
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly assembly in assemblies)
			{
				Type[] array;
				try
				{
					array = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					array = ex.Types.Where((Type t) => t != null).ToArray();
				}
				catch
				{
					continue;
				}
				Type[] array2 = array;
				foreach (Type type in array2)
				{
					if (!(type == null) && !type.IsAbstract && typeof(Def).IsAssignableFrom(type))
					{
						hashSet.Add(type);
					}
				}
			}
			foreach (Type item in hashSet)
			{
				try
				{
					Type type2 = typeof(DefDatabase<>).MakeGenericType(item);
					PropertyInfo property = type2.GetProperty("AllDefsListForReading", BindingFlags.Static | BindingFlags.Public);
					if (property == null || !(property.GetValue(null, null) is IEnumerable enumerable))
					{
						continue;
					}
					summary.RuntimeDefTypesScanned++;
					foreach (object item2 in enumerable)
					{
						if (!(item2 is Def { modContentPack: not null } def) || string.IsNullOrWhiteSpace(def.modContentPack.PackageId) || string.IsNullOrWhiteSpace(def.defName))
						{
							continue;
						}
						FieldInfo[] fields = def.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
						foreach (FieldInfo fieldInfo in fields)
						{
							if (!(fieldInfo.FieldType != typeof(string)) && IsTranslatableDefField(fieldInfo.Name))
							{
								string text;
								try
								{
									text = fieldInfo.GetValue(def) as string;
								}
								catch
								{
									continue;
								}
								if (!string.IsNullOrWhiteSpace(text) && !IsTechnicalText(fieldInfo.Name, text))
								{
									AddSourceEntry(def.modContentPack, "<runtime DefDatabase>", item.Name, def.defName + "." + fieldInfo.Name, text, database, summary);
									summary.RuntimeDefFieldsExtracted++;
								}
							}
						}
					}
				}
				catch (Exception ex2)
				{
					MLog.Warn("[Universal RimTranslator] Runtime Def scan skipped " + item.FullName + ": " + ex2.GetType().Name + ": " + ex2.Message);
				}
			}
		}

		private static void ScanCoreFallback(TranslationDatabase database, ScanSummary summary)
		{
			try
			{
				ModContentPack modContentPack = null;
				foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
				{
					if (item == null)
					{
						continue;
					}
					string text = item.RootDir ?? "";
					if (!string.Equals(item.Name, "Core", StringComparison.OrdinalIgnoreCase))
					{
						char directorySeparatorChar = Path.DirectorySeparatorChar;
						if (!text.EndsWith(directorySeparatorChar + "Core", StringComparison.OrdinalIgnoreCase))
						{
							directorySeparatorChar = Path.AltDirectorySeparatorChar;
							if (!text.EndsWith(directorySeparatorChar + "Core", StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}
						}
					}
					modContentPack = item;
					break;
				}
				string text2 = ((modContentPack != null) ? modContentPack.RootDir : FindCoreRoot());
				if (string.IsNullOrWhiteSpace(text2) || !Directory.Exists(text2))
				{
					return;
				}
				string text3 = ((modContentPack != null && !string.IsNullOrWhiteSpace(modContentPack.PackageId)) ? modContentPack.PackageId : "ludeon.rimworld.core");
				string text4 = ((modContentPack != null && !string.IsNullOrWhiteSpace(modContentPack.Name)) ? modContentPack.Name : "RimWorld Core");
				if (database.HasPackageLanguage(text3, "English"))
				{
					return;
				}
				string path = Path.Combine(text2, "Languages");
				string text5 = Path.Combine(path, "English");
				if (Directory.Exists(text5))
				{
					if (!summary.Languages.Contains("English", StringComparer.OrdinalIgnoreCase))
					{
						summary.Languages.Add("English");
					}
					ScanCoreType(text4, text3, text5, "Keyed", Path.Combine(text5, "Keyed"), database, summary);
					ScanCoreType(text4, text3, text5, "DefInjected", Path.Combine(text5, "DefInjected"), database, summary);
				}
				string path2 = Path.Combine(text2, "Defs");
				if (Directory.Exists(path2))
				{
					string[] files = Directory.GetFiles(path2, "*.xml", SearchOption.AllDirectories);
					foreach (string text6 in files)
					{
						summary.FilesScanned++;
						summary.CoreFilesScanned++;
						try
						{
							XDocument xDocument = XDocument.Load(text6, LoadOptions.PreserveWhitespace);
							if (xDocument.Root != null)
							{
								ExtractSourceDefsCore(text4, text3, text6, xDocument.Root, database, summary);
							}
						}
						catch (Exception ex)
						{
							MLog.Warn("[Universal RimTranslator] Core Defs XML parse failed: " + text6 + " | " + ex.Message);
						}
					}
				}
				MLog.Info("[Universal RimTranslator] Core fallback scan complete: " + text4 + " | package=" + text3 + " | root=" + text2);
			}
			catch (Exception ex2)
			{
				MLog.Warn("[Universal RimTranslator] Core fallback scan failed: " + ex2.GetType().Name + ": " + ex2.Message);
			}
		}

		private static void ScanCoreType(string modName, string packageId, string languageRoot, string type, string directory, TranslationDatabase database, ScanSummary summary)
		{
			if (!Directory.Exists(directory))
			{
				return;
			}
			string[] files = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories);
			foreach (string text in files)
			{
				summary.FilesScanned++;
				summary.CoreFilesScanned++;
				try
				{
					XDocument xDocument = XDocument.Load(text, LoadOptions.PreserveWhitespace);
					if (xDocument.Root == null)
					{
						continue;
					}
					foreach (XElement item in xDocument.Root.DescendantsAndSelf())
					{
						if (item.HasElements || string.IsNullOrWhiteSpace(item.Value))
						{
							continue;
						}
						string localName = item.Name.LocalName;
						if (string.Equals(type, "DefInjected", StringComparison.OrdinalIgnoreCase))
						{
							if (string.Equals(xDocument.Root.Name.LocalName, "LanguageData", StringComparison.OrdinalIgnoreCase))
							{
								string defTypeFromPath = GetDefTypeFromPath(text);
								if (!string.IsNullOrEmpty(defTypeFromPath) && !IsTechnicalText(localName, item.Value))
								{
									AddEntryCore(modName, packageId, "English", text, "DefInjected", defTypeFromPath, localName, item.Value, database, summary);
								}
							}
						}
						else if (!IsTechnicalText(localName, item.Value))
						{
							AddEntryCore(modName, packageId, "English", text, "Keyed", "", localName, item.Value, database, summary);
						}
					}
				}
				catch (Exception ex)
				{
					MLog.Warn("[Universal RimTranslator] Core language XML parse failed: " + text + " | " + ex.Message);
				}
			}
		}

		private static void ExtractSourceDefsCore(string modName, string packageId, string file, XElement root, TranslationDatabase database, ScanSummary summary)
		{
			IEnumerable<XElement> enumerable = root.Elements().Where(HasDefName);
			if (HasDefName(root))
			{
				enumerable = new XElement[1] { root };
			}
			foreach (XElement item in enumerable)
			{
				string directChildValue = GetDirectChildValue(item, "defName");
				if (string.IsNullOrWhiteSpace(directChildValue))
				{
					continue;
				}
				summary.SourceDefsFound++;
				string localName = item.Name.LocalName;
				foreach (XElement item2 in item.Elements())
				{
					if (!item2.HasElements && !string.IsNullOrWhiteSpace(item2.Value))
					{
						string localName2 = item2.Name.LocalName;
						if (IsTranslatableDefField(localName2) && !IsTechnicalText(localName2, item2.Value))
						{
							AddEntryCore(modName, packageId, "English", file, "DefInjected", localName, directChildValue + "." + localName2, item2.Value, database, summary);
							summary.SourceFieldsExtracted++;
						}
					}
				}
				foreach (XElement item3 in from x in item.Descendants()
					where !x.HasElements
					select x)
				{
					if (item3.Parent == item || string.IsNullOrWhiteSpace(item3.Value))
					{
						continue;
					}
					string localName3 = item3.Name.LocalName;
					if ((IsNestedTextField(localName3) || IsRulesStringLeaf(item3)) && (!IsTechnicalText(localName3, item3.Value) || IsRulesStringLeaf(item3)))
					{
						string text = BuildRelativePath(item, item3);
						if (!string.IsNullOrEmpty(text))
						{
							AddEntryCore(modName, packageId, "English", file, "DefInjected", localName, directChildValue + "." + text, item3.Value, database, summary);
							summary.SourceFieldsExtracted++;
						}
					}
				}
			}
		}

		private static void AddEntryCore(string modName, string packageId, string language, string file, string type, string defType, string key, string text, TranslationDatabase database, ScanSummary summary)
		{
			if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text) || HitEntryCap(summary, modName, packageId))
			{
				return;
			}
			if (ShouldSkipCached(packageId, type, defType, key, text))
			{
				if (summary != null)
				{
					summary.EntriesSkippedCached++;
				}
				skippedCached++;
				return;
			}
			database.Add(new TranslationEntry
			{
				ModName = (modName ?? "RimWorld Core"),
				ModPackageId = (packageId ?? "ludeon.rimworld.core"),
				Language = (language ?? "English"),
				DefType = (defType ?? ""),
				Key = key,
				Text = text.Trim(),
				FilePath = file,
				Type = type
			});
			summary.EntriesExtracted++;
			summary.CoreEntriesExtracted++;
			currentModNewEntries++;
			RegisterTranslatableMod(summary, packageId, modName);
		}

		private static string FindCoreRoot()
		{
			try
			{
				string location = Assembly.GetExecutingAssembly().Location;
				DirectoryInfo directoryInfo = (string.IsNullOrEmpty(location) ? null : Directory.GetParent(location));
				DirectoryInfo directoryInfo2 = ((directoryInfo == null) ? null : Directory.GetParent(directoryInfo.FullName));
				if (directoryInfo2 == null)
				{
					return "";
				}
				DirectoryInfo directoryInfo3 = directoryInfo2;
				int num = 0;
				while (num < 6 && directoryInfo3 != null)
				{
					DirectoryInfo directoryInfo4 = new DirectoryInfo(Path.Combine(directoryInfo3.FullName, "Core"));
					if (directoryInfo4.Exists && Directory.Exists(Path.Combine(directoryInfo4.FullName, "Defs")))
					{
						return directoryInfo4.FullName;
					}
					DirectoryInfo directoryInfo5 = new DirectoryInfo(Path.Combine(directoryInfo3.FullName, "Mods"));
					if (directoryInfo5.Exists)
					{
						directoryInfo4 = new DirectoryInfo(Path.Combine(directoryInfo5.FullName, "Core"));
						if (directoryInfo4.Exists && Directory.Exists(Path.Combine(directoryInfo4.FullName, "Defs")))
						{
							return directoryInfo4.FullName;
						}
					}
					num++;
					directoryInfo3 = directoryInfo3.Parent;
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] Core folder lookup failed: " + ex.Message);
			}
			return "";
		}

		private static bool HasDefName(XElement x)
		{
			return x.Elements().Any((XElement e) => string.Equals(e.Name.LocalName, "defName", StringComparison.OrdinalIgnoreCase));
		}

		private static string BuildRelativePath(XElement root, XElement leaf)
		{
			List<string> list = new List<string>();
			XElement current = leaf;
			while (current != null && current != root)
			{
				XElement parent = current.Parent;
				if (parent != null)
				{
					IEnumerable<XElement> source = parent.Elements(current.Name);
					int num = source.TakeWhile((XElement x) => x != current).Count();
					int num2 = source.Count();
					if (num2 > 1)
					{
						list.Add(num.ToString());
					}
					else
					{
						list.Add(current.Name.LocalName);
					}
				}
				else
				{
					list.Add(current.Name.LocalName);
				}
				current = parent;
			}
			if (current != root)
			{
				return "";
			}
			list.Reverse();
			return string.Join(".", list.ToArray());
		}

		private static bool IsNestedTextField(string field)
		{
			switch ((field ?? "").Trim().ToLowerInvariant())
			{
			case "label":
			case "labelshort":
			case "labelplural":
			case "description":
			case "text":
			case "message":
			case "reportstring":
			case "jobstring":
			case "gerund":
			case "verb":
			case "title":
			case "titleshort":
			case "inspectstring":
			case "customlabel":
			case "letterlabel":
			case "lettertext":
			case "pawnlabel":
			case "thinglabel":
			case "labelsocial":
			case "headertip":
			case "labelmale":
			case "labelfemale":
			case "uselabel":
			case "gerundlabel":
			case "stufflabel":
			case "tooltiplabel":
			case "deathmessage":
			case "ingestcommandstring":
			case "ingestreporstring":
			case "ingestreportstring":
			case "destroyMessage":
			case "destroymessage":
			case "discoveredletterlabel":
			case "discoveredlettertext":
			case "beginletter":
			case "beginletterlabel":
			case "recoveryMessage":
			case "recoverymessage":
			case "extraTooltip":
			case "extratooltip":
			case "stuffAdjective":
			case "stuffadjective":
			case "labelnoun":
			case "labelnounpretty":
			case "descriptionshort":
			case "helptext":
			case "helpText":
			case "baselinestring":
			case "inspectstringextra":
			case "customdescription":
			case "effectdescription":
			case "letterdef":
			case "oneliner":
			case "quote":
			case "flavortext":
			case "flavourtext":
			case "outcomelabel":
			case "outcometext":
			case "rolelabel":
			case "spectatorlabel":
			case "jobreportstring":
			case "jobreport":
			case "gizmolabel":
			case "commandlabel":
			case "commanddesc":
			case "desc":
			case "tooltip":
			case "tip":
			case "productdescription":
			case "researchdescription":
			case "discoveredMessage":
			case "discoveredmessage":
			case "labelusedinevent":
			case "gerunds":
				return true;
			default:
			{
				string text = (field ?? "").Trim().ToLowerInvariant();
				if (text.Length == 0)
				{
					return false;
				}
				if (text.Contains("label") || text.Contains("desc") || text.Contains("text") || text.Contains("message") || text.Contains("tip") || text.Contains("letter") || text.Contains("help") || text.Contains("title") || text.Contains("flavor") || text.Contains("quote") || text.Contains("verb") || text.Contains("gerund") || text.Contains("inspect") || text.EndsWith("string"))
				{
					return true;
				}
				return false;
			}
			}
		}

		private static bool IsTranslatableDefField(string field)
		{
			return IsNestedTextField(field);
		}

		private static bool IsTechnicalText(string key, string value)
		{
			string text = (key ?? "").Trim();
			string text2 = (value ?? "").Trim();
			if (string.IsNullOrEmpty(text2))
			{
				return true;
			}
			if (text.Equals("defName", StringComparison.OrdinalIgnoreCase) || text.Equals("packageId", StringComparison.OrdinalIgnoreCase) || text.Equals("li", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (UrtNonText.IsNonTextField(text) || UrtNonText.LooksLikeNonText(text2))
			{
				return true;
			}
			if (Regex.IsMatch(text2, "^\\$[A-Za-z_][A-Za-z0-9_.]*$"))
			{
				return true;
			}
			if (Regex.IsMatch(text2, "^\\{[0-9]+\\}$"))
			{
				return true;
			}
			if (Regex.IsMatch(text2, "^\\\\(?:r\\\\n|n|r|t)$"))
			{
				return true;
			}
			if (text2.Length > 0 && text2.StartsWith("<", StringComparison.Ordinal) && text2.EndsWith(">", StringComparison.Ordinal))
			{
				return true;
			}
			if (!text2.Any(char.IsLetterOrDigit))
			{
				return true;
			}
			return false;
		}

		private static string GetDefTypeFromPath(string file)
		{
			string text = file.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
			char directorySeparatorChar = Path.DirectorySeparatorChar;
			string text2 = directorySeparatorChar.ToString();
			directorySeparatorChar = Path.DirectorySeparatorChar;
			string text3 = text2 + "DefInjected" + directorySeparatorChar;
			int num = text.IndexOf(text3, StringComparison.OrdinalIgnoreCase);
			if (num < 0)
			{
				return "";
			}
			string text4 = text.Substring(num + text3.Length);
			int num2 = text4.IndexOf(Path.DirectorySeparatorChar);
			return (num2 < 0) ? "" : text4.Substring(0, num2).Trim();
		}

		private static string GetDirectChildValue(XElement element, string name)
		{
			XElement xElement = element.Elements().FirstOrDefault((XElement e) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
			return (xElement == null) ? "" : xElement.Value.Trim();
		}

		private static void AddSourceEntry(ModContentPack content, string file, string type, string key, string text, TranslationDatabase database, ScanSummary summary)
		{
			string language = LanguageDetector.DetectEmbeddedSourceLanguage(text);
			AddEntry(content, language, file, "DefInjected", type, key, text, database, summary);
		}

		private static void CountLanguage(string packageId, string language, string file)
		{
			if (string.IsNullOrEmpty(packageId))
			{
				return;
			}
			string text = file ?? "";
			if (text.StartsWith("<", StringComparison.Ordinal))
			{
				return;
			}
			string text2 = LanguageCodeMapper.Canonical(LanguageNormalizer.Normalize(language ?? ""));
			if (string.IsNullOrEmpty(text2))
			{
				text2 = "English";
			}
			lock (LastLanguageStats)
			{
				if (!LastLanguageStats.TryGetValue(packageId, out var value))
				{
					value = new LanguageStats();
					LastLanguageStats[packageId] = value;
				}
				string text3 = text.Replace('\\', '/');
				Dictionary<string, int> dictionary = ((text3.IndexOf("/Languages/", StringComparison.OrdinalIgnoreCase) >= 0) ? value.Packs : value.Defs);
				dictionary.TryGetValue(text2, out var value2);
				dictionary[text2] = value2 + 1;
			}
		}

		private static void AddEntry(ModContentPack content, string language, string file, string type, string defType, string key, string text, TranslationDatabase database, ScanSummary summary)
		{
			if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			CountLanguage((content == null) ? "" : content.PackageId, language, file);
			if (HitEntryCap(summary, (content == null) ? "" : content.Name, (content == null) ? "" : content.PackageId))
			{
				return;
			}
			if (ShouldSkipCached((content == null) ? "" : content.PackageId, type, defType, key, text))
			{
				if (summary != null)
				{
					summary.EntriesSkippedCached++;
				}
				skippedCached++;
				return;
			}
			database.Add(new TranslationEntry
			{
				ModName = (content.Name ?? ""),
				ModPackageId = (content.PackageId ?? ""),
				Language = (language ?? ""),
				DefType = (defType ?? ""),
				Key = key,
				Text = text.Trim(),
				FilePath = file,
				Type = type
			});
			summary.EntriesExtracted++;
			currentModNewEntries++;
			RegisterTranslatableMod(summary, (content == null) ? "" : content.PackageId, (content == null) ? "" : content.Name);
		}

		private static void RegisterTranslatableMod(ScanSummary summary, string packageId, string modName)
		{
			if (summary != null)
			{
				string item = ((!string.IsNullOrWhiteSpace(packageId)) ? packageId.Trim() : ((!string.IsNullOrWhiteSpace(modName)) ? modName.Trim() : "<unknown>"));
				if (summary.TranslatableModIds.Add(item))
				{
					summary.ModsWithTranslatableText = summary.TranslatableModIds.Count;
				}
			}
		}

		private static string GetTranslatorRoot()
		{
			try
			{
				string location = Assembly.GetExecutingAssembly().Location;
				DirectoryInfo directoryInfo = (string.IsNullOrEmpty(location) ? null : Directory.GetParent(location));
				DirectoryInfo directoryInfo2 = ((directoryInfo == null) ? null : Directory.GetParent(directoryInfo.FullName));
				return (directoryInfo2 == null) ? "" : directoryInfo2.FullName;
			}
			catch
			{
				return "";
			}
		}

		private static bool PathsEqual(string a, string b)
		{
			try
			{
				string a2 = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				string b2 = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				return string.Equals(a2, b2, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}
	}
	public sealed class MyMemoryTranslateProvider : IUniversalTranslationProvider
	{
		private readonly HttpClient client = CreateClient();

		public string Name => "MyMemory";

		private static HttpClient CreateClient()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected O, but got Unknown
			HttpClient val = new HttpClient();
			val.Timeout = TimeSpan.FromSeconds(18.0);
			return val;
		}

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Batch is empty.";
				return result;
			}
			string endpoint = ((settings != null && !string.IsNullOrWhiteSpace(settings.MyMemoryEndpoint)) ? settings.MyMemoryEndpoint.Trim() : "https://api.mymemory.translated.net/get");
			if (UrtOptEngine.IsCoolingDown(Name))
			{
				result.Error = "MyMemory cooldown active (" + UrtOptEngine.CooldownRemainingSeconds(Name) + "s)";
				return result;
			}
			try
			{
				foreach (string text in texts)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (UrtOptEngine.IsCoolingDown(Name))
					{
						result.Translations.Add("");
						continue;
					}
					string input = text ?? "";
					string src = Code(source);
					int num;
					switch (src)
					{
					default:
						num = (string.IsNullOrEmpty(src) ? 1 : 0);
						break;
					case "en":
					case "auto":
					case "aut":
						num = 1;
						break;
					}
					if (num != 0)
					{
						src = GuessMemSource(input);
					}
					string url = endpoint + "?q=" + Uri.EscapeDataString(input) + "&langpair=" + Uri.EscapeDataString(src + "|" + Code(target));
					if (settings != null && !string.IsNullOrWhiteSpace(settings.MyMemoryEmail))
					{
						url = url + "&de=" + Uri.EscapeDataString(settings.MyMemoryEmail.Trim());
					}
					string json = "";
					bool ok = false;
					string itemError = "";
					for (int attempt = 0; attempt < 3; attempt++)
					{
						object obj;
						int num2;
						try
						{
							HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
							try
							{
								result.StatusCode = (int)response.StatusCode;
								json = await response.Content.ReadAsStringAsync();
								if (response.IsSuccessStatusCode)
								{
									ok = true;
									break;
								}
								itemError = "HTTP " + result.StatusCode + ": " + json;
								if (result.StatusCode == 429)
								{
									int waitS = 120;
									if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
									{
										waitS = Math.Max(30, Math.Min(600, (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds));
									}
									UrtOptEngine.CoolDown(Name, waitS);
								}
							}
							finally
							{
								((IDisposable)response)?.Dispose();
							}
						}
						catch (TaskCanceledException ex4)
						{
							TaskCanceledException ex2 = ex4;
							itemError = "Timeout: " + ex2.Message;
						}
						catch (HttpRequestException val)
						{
							HttpRequestException val2 = val;
							obj = val2;
							num2 = 1;
							goto IL_056c;
						}
						break;
						IL_056c:
						if (num2 == 1)
						{
							HttpRequestException ex3 = (HttpRequestException)obj;
							itemError = "Network error: " + ((Exception)(object)ex3).Message;
							if (attempt >= 2)
							{
								break;
							}
							await Task.Delay(750 * (attempt + 1), cancellationToken);
						}
					}
					if (!ok)
					{
						result.Translations.Add("");
						if (string.IsNullOrWhiteSpace(result.Error))
						{
							result.Error = "MyMemory item failed: " + (string.IsNullOrWhiteSpace(itemError) ? "unknown error" : itemError);
						}
						continue;
					}
					string translated = ProviderJsonHelper.ExtractFirstString(json, "translatedText");
					if (IsQuotaMessage(translated) || IsQuotaMessage(ProviderJsonHelper.ExtractFirstString(json, "responseDetails")))
					{
						UrtOptEngine.CoolDown(Name, 3600);
						result.StatusCode = 429;
						result.Error = "MyMemory: daily free quota used up (cooldown 1 h)";
						while (result.Translations.Count < texts.Count)
						{
							result.Translations.Add("");
						}
						break;
					}
					if (!string.IsNullOrWhiteSpace(translated) && (translated.IndexOf("LANGPAIR", StringComparison.OrdinalIgnoreCase) >= 0 || translated.IndexOf("INVALID SOURCE LANGUAGE", StringComparison.OrdinalIgnoreCase) >= 0))
					{
						result.Translations.Add("");
					}
					else if (string.IsNullOrWhiteSpace(translated))
					{
						string detail = ProviderJsonHelper.ExtractFirstString(json, "responseDetails");
						result.Translations.Add("");
						if (string.IsNullOrWhiteSpace(result.Error))
						{
							result.Error = "MyMemory returned no translation" + (string.IsNullOrWhiteSpace(detail) ? "." : (": " + detail));
						}
					}
					else
					{
						result.Translations.Add(WebUtility.HtmlDecode(translated));
					}
				}
				while (result.Translations.Count < texts.Count)
				{
					result.Translations.Add("");
				}
				result.Success = result.Translations.Exists((string t) => !string.IsNullOrWhiteSpace(t));
				if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
				{
					result.Error = "MyMemory returned no usable translation.";
				}
				foreach (string text2 in texts)
				{
					result.CharacterCount += (text2 ?? "").Length;
				}
				return result;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				result.Error = ex.GetType().Name + ": " + ex.Message;
				return result;
			}
		}

		private static bool IsQuotaMessage(string text)
		{
			return !string.IsNullOrEmpty(text) && (text.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("USED ALL AVAILABLE FREE TRANSLATIONS", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		private static string GuessMemSource(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "en";
			}
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			foreach (char c in text)
			{
				if (c >= '\u3040' && c <= 'ヿ')
				{
					num++;
				}
				else if (c >= '가' && c <= '\ud7af')
				{
					num2++;
				}
				else if (c >= '一' && c <= '\u9fff')
				{
					num3++;
				}
				else if (c >= 'Ѐ' && c <= 'ӿ')
				{
					num4++;
				}
			}
			if (num > 0)
			{
				return "ja";
			}
			if (num2 > 0)
			{
				return "ko";
			}
			if (num3 > 0)
			{
				return "zh";
			}
			if (num4 > 0)
			{
				return "ru";
			}
			return "en";
		}

		private static string Code(string language)
		{
			switch ((language ?? "").Trim().ToLowerInvariant())
			{
			case "english":
				return "en";
			case "czech":
				return "cs";
			case "german":
				return "de";
			case "french":
				return "fr";
			case "italian":
				return "it";
			case "japanese":
				return "ja";
			case "korean":
				return "ko";
			case "polish":
				return "pl";
			case "portuguese":
			case "portuguesebrazilian":
				return "pt";
			case "russian":
				return "ru";
			case "spanish":
				return "es";
			case "dutch":
				return "nl";
			case "danish":
				return "da";
			case "swedish":
				return "sv";
			case "norwegian":
				return "no";
			case "ukrainian":
				return "uk";
			case "chinesesimplified":
			case "chinesetraditional":
			case "chinese":
				return "zh";
			case "turkish":
				return "tr";
			case "vietnamese":
				return "vi";
			case "arabic":
				return "ar";
			case "bulgarian":
				return "bg";
			case "croatian":
				return "hr";
			case "finnish":
				return "fi";
			case "greek":
				return "el";
			case "hebrew":
				return "he";
			case "hungarian":
				return "hu";
			case "indonesian":
				return "id";
			case "romanian":
				return "ro";
			case "slovak":
				return "sk";
			case "slovenian":
				return "sl";
			case "thai":
				return "th";
			case "estonian":
				return "et";
			case "latvian":
				return "lv";
			case "lithuanian":
				return "lt";
			case "auto":
			case "aut":
			case "":
				return "en";
			default:
			{
				string text = (language ?? "").Trim().ToLowerInvariant();
				if (text == "auto" || text == "aut" || text.Length > 3)
				{
					return "en";
				}
				return text;
			}
			}
		}
	}
	internal static class ProviderJsonHelper
	{
		public static List<string> ExtractStringValues(string json, string propertyName)
		{
			List<string> list = new List<string>();
			if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
			{
				return list;
			}
			string text = "\"" + propertyName + "\"";
			int startIndex = 0;
			while (true)
			{
				int num = json.IndexOf(text, startIndex, StringComparison.Ordinal);
				if (num < 0)
				{
					break;
				}
				int i;
				for (i = num + text.Length; i < json.Length && char.IsWhiteSpace(json[i]); i++)
				{
				}
				if (i >= json.Length || json[i] != ':')
				{
					startIndex = num + 1;
					continue;
				}
				for (i++; i < json.Length && char.IsWhiteSpace(json[i]); i++)
				{
				}
				if (i < json.Length && json[i] == '"')
				{
					if (TryReadJsonString(json, i, out var value, out var after))
					{
						list.Add(value);
						startIndex = after;
					}
					else
					{
						startIndex = i + 1;
					}
				}
				else if (i < json.Length && json[i] == '[')
				{
					int num2 = FindMatching(json, i, '[', ']');
					if (num2 > i)
					{
						int j = i + 1;
						while (j < num2)
						{
							for (; j < num2 && (char.IsWhiteSpace(json[j]) || json[j] == ','); j++)
							{
							}
							if (j >= num2 || json[j] != '"' || !TryReadJsonString(json, j, out var value2, out var after2))
							{
								break;
							}
							list.Add(value2);
							j = after2;
						}
					}
					startIndex = ((num2 > i) ? (num2 + 1) : (i + 1));
				}
				else
				{
					startIndex = i + 1;
				}
			}
			return list;
		}

		public static string ExtractFirstString(string json, string propertyName)
		{
			List<string> list = ExtractStringValues(json, propertyName);
			return (list.Count == 0) ? null : list[0];
		}

		public static string JsonEscape(string text)
		{
			StringBuilder stringBuilder = new StringBuilder();
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				switch (c)
				{
				case '\\':
					stringBuilder.Append("\\\\");
					continue;
				case '"':
					stringBuilder.Append("\\\"");
					continue;
				case '\r':
					stringBuilder.Append("\\r");
					continue;
				case '\n':
					stringBuilder.Append("\\n");
					continue;
				case '\t':
					stringBuilder.Append("\\t");
					continue;
				case '\b':
					stringBuilder.Append("\\b");
					continue;
				case '\f':
					stringBuilder.Append("\\f");
					continue;
				}
				if (c < ' ')
				{
					StringBuilder stringBuilder2 = stringBuilder.Append("\\u");
					int num = c;
					stringBuilder2.Append(num.ToString("x4"));
				}
				else
				{
					stringBuilder.Append(c);
				}
			}
			return stringBuilder.ToString();
		}

		private static bool TryReadJsonString(string json, int quote, out string value)
		{
			int after;
			return TryReadJsonString(json, quote, out value, out after);
		}

		private static bool TryReadJsonString(string json, int quote, out string value, out int after)
		{
			value = null;
			after = quote + 1;
			if (quote < 0 || quote >= json.Length || json[quote] != '"')
			{
				return false;
			}
			StringBuilder stringBuilder = new StringBuilder();
			bool flag = false;
			for (int i = quote + 1; i < json.Length; i++)
			{
				char c = json[i];
				if (flag)
				{
					switch (c)
					{
					case '"':
						stringBuilder.Append('"');
						break;
					case '\\':
						stringBuilder.Append('\\');
						break;
					case '/':
						stringBuilder.Append('/');
						break;
					case 'b':
						stringBuilder.Append('\b');
						break;
					case 'f':
						stringBuilder.Append('\f');
						break;
					case 'n':
						stringBuilder.Append('\n');
						break;
					case 'r':
						stringBuilder.Append('\r');
						break;
					case 't':
						stringBuilder.Append('\t');
						break;
					case 'u':
					{
						if (i + 4 <= json.Length - 1 && ushort.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, null, out var result))
						{
							stringBuilder.Append((char)result);
							i += 4;
						}
						break;
					}
					default:
						stringBuilder.Append(c);
						break;
					}
					flag = false;
				}
				else
				{
					switch (c)
					{
					case '\\':
						flag = true;
						break;
					case '"':
						value = stringBuilder.ToString();
						after = i + 1;
						return true;
					default:
						stringBuilder.Append(c);
						break;
					}
				}
			}
			return false;
		}

		private static int FindMatching(string json, int start, char open, char close)
		{
			int num = 0;
			bool flag = false;
			bool flag2 = false;
			for (int i = start; i < json.Length; i++)
			{
				char c = json[i];
				if (flag)
				{
					if (flag2)
					{
						flag2 = false;
						continue;
					}
					switch (c)
					{
					case '\\':
						flag2 = true;
						break;
					case '"':
						flag = false;
						break;
					}
				}
				else if (c == '"')
				{
					flag = true;
				}
				else if (c == open)
				{
					num++;
				}
				else if (c == close)
				{
					num--;
					if (num == 0)
					{
						return i;
					}
				}
			}
			return -1;
		}
	}
	public static class RuntimeLiveQueue
	{
		private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();

		private static readonly ConcurrentQueue<KeyValuePair<string, string>> Completed = new ConcurrentQueue<KeyValuePair<string, string>>();

		private static readonly ConcurrentQueue<KeyValuePair<string, string>> PersistencePending = new ConcurrentQueue<KeyValuePair<string, string>>();

		private static readonly ConcurrentDictionary<string, byte> Closed = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, int> Attempts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, Task<string>> InFlight = new ConcurrentDictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

		private static readonly Regex Token = new Regex("(\\{\\{[0-9]+\\}\\}|\\{[0-9A-Za-z_:\\.]+\\}|\\$[A-Za-z_][A-Za-z0-9_\\.]*|\\[\\[[^\\]]+\\]\\]|\\[[^\\]]+\\]|<[^>\\r\\n]+>|\\\\n|\\(\\*[A-Za-z_][^)\\r\\n]*\\)|\\(/[A-Za-z_][A-Za-z0-9_]*\\))", RegexOptions.Compiled);

		private static readonly Regex Ident = new Regex("^[A-Za-z][A-Za-z0-9_\\.]{2,}$", RegexOptions.Compiled);

		private static readonly ConcurrentQueue<string> SkipWrites = new ConcurrentQueue<string>();

		private static readonly ConcurrentDictionary<string, byte> Considered = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

		private static int enqueueFrame;

		private static int enqueueThisFrame;

		private static int azureFailLog;

		private static int azureCooldownLog;

		private static int providerDiagLog;

		private const int MaxEnqueuePerFrame = 24;

		private const int MaxPending = 400;

		private const int MaxLiveWorkers = 4;

		private const int MaxPumpPerTick = 30;

		private static int running;

		private static int persistenceRunning;

		private static long persistenceLastQueuedTicks;

		private static int laneRr;

		private static int done;

		private static bool loggedB74;

		private static bool loggedB75;

		private static int closedCount;

		private static int lastLog;

		private static int skipLoaded;

		private static readonly ConcurrentDictionary<string, int> Delayed = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		private static int lastDelayedPump;

		private const int MaxDelayed = 2000;

		[ThreadStatic]
		private static HashSet<string> triedThisPass;

		private const int LiveHttpTimeoutMs = 12000;

		private static int customBusy;

		private static volatile bool paused;

		private static volatile bool userEnabled = false;

		private static readonly ConcurrentDictionary<string, byte> Held = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

		private static readonly ConcurrentDictionary<string, int> nextTry = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		private const bool SpliceEnglishRunsEnabled = false;

		private static readonly ConcurrentDictionary<string, FieldInfo> SettingsFields = new ConcurrentDictionary<string, FieldInfo>(StringComparer.Ordinal);

		public static bool Paused => paused;

		public static bool UserEnabled => userEnabled;

		public static bool Active => userEnabled && !paused;

		public static int PendingCount => Pending.Count;

		public static int DoneCount => done;

		public static int ClosedCount => closedCount;

		public static void Hold(string text)
		{
			if (!string.IsNullOrWhiteSpace(text))
			{
				Held[text.Trim()] = 1;
			}
		}

		public static void ReleaseAllHolds()
		{
			Held.Clear();
		}

		public static void SetUserEnabled(bool on)
		{
			userEnabled = on;
		}

		public static void Pause()
		{
			paused = true;
		}

		public static void Resume()
		{
			paused = false;
		}

		public static void PumpCompleted()
		{
			int i;
			for (i = 0; i < 30; i++)
			{
				if (!Completed.TryDequeue(out var result))
				{
					break;
				}
				try
				{
					RuntimeTextTranslator.RegisterLive(result.Key, result.Value);
				}
				catch
				{
				}
				PersistencePending.Enqueue(result);
				Interlocked.Exchange(ref persistenceLastQueuedTicks, DateTime.UtcNow.Ticks);
			}
			if (i > 0 || !SkipWrites.IsEmpty)
			{
				TryStartPersistence();
			}
			PumpDelayed();
		}

		private static void ScheduleRetry(string text, int delayMs)
		{
			if (!string.IsNullOrWhiteSpace(text))
			{
				if (Delayed.Count >= 2000)
				{
					RuntimeTextTranslator.ReleaseMiss(text);
				}
				else
				{
					Delayed[text] = Environment.TickCount + Math.Max(1000, delayMs);
				}
			}
		}

		private static void PumpDelayed()
		{
			int tickCount = Environment.TickCount;
			if (tickCount - lastDelayedPump < 500)
			{
				return;
			}
			lastDelayedPump = tickCount;
			bool flag = false;
			if (!Delayed.IsEmpty)
			{
				foreach (KeyValuePair<string, int> item in Delayed)
				{
					if (tickCount - item.Value >= 0 && Delayed.TryRemove(item.Key, out var _) && Pending.Count < 800)
					{
						Pending.Enqueue(item.Key);
						flag = true;
					}
				}
			}
			if (Closed.Count > 60000)
			{
				Closed.Clear();
				Interlocked.Exchange(ref skipLoaded, 0);
			}
			if (Attempts.Count > 20000)
			{
				Attempts.Clear();
			}
			if (Considered.Count > 60000)
			{
				Considered.Clear();
			}
			if (nextTry.Count > 20000)
			{
				foreach (KeyValuePair<string, int> item2 in nextTry)
				{
					if (tickCount - item2.Value > 60000)
					{
						nextTry.TryRemove(item2.Key, out var _);
					}
				}
			}
			if (flag && Active)
			{
				TryStart();
			}
		}

		public static bool AlreadySeen(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return true;
			}
			return Considered.ContainsKey(text) || Closed.ContainsKey(text);
		}

		public static bool HasForeignRun(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			int num = 0;
			for (int i = 0; i < text.Length; i++)
			{
				if (IsForeignLetter(text[i]))
				{
					num++;
					if (num >= 2)
					{
						return true;
					}
				}
				else
				{
					num = 0;
				}
			}
			return false;
		}

		public static void EnqueueVisibleForeign(string text)
		{
			if (paused || !userEnabled)
			{
				return;
			}
			if (UrtLive2.Engaged)
			{
				UrtLive2.Enqueue(text);
			}
			else
			{
				if (Current.ProgramState != ProgramState.Playing || string.IsNullOrWhiteSpace(text) || text.Length > 8000 || text.Length < 2 || !NeedsLive(text) || text.IndexOf("Universal RimTranslator", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return;
				}
				string text2 = text.Trim();
				if (Held.ContainsKey(text2))
				{
					return;
				}
				if (Closed.ContainsKey(text2))
				{
					int tickCount = Environment.TickCount;
					if (nextTry.TryGetValue(text2, out var value) && tickCount - value < 8000)
					{
						return;
					}
					Closed.TryRemove(text2, out var value2);
					Considered.TryRemove(text2, out value2);
					Attempts.TryRemove(text2, out var _);
					nextTry[text2] = tickCount;
				}
				else if (Considered.ContainsKey(text2))
				{
					return;
				}
				int frameCount = Time.frameCount;
				if (frameCount != enqueueFrame)
				{
					enqueueFrame = frameCount;
					enqueueThisFrame = 0;
				}
				if (enqueueThisFrame >= 24)
				{
					return;
				}
				string result;
				while (Pending.Count >= 400 && Pending.TryDequeue(out result))
				{
					Considered.TryRemove(result, out var _);
					Attempts.TryRemove(result, out var _);
				}
				if (Attempts.TryAdd(text2, 0))
				{
					enqueueThisFrame++;
					Considered.TryAdd(text2, 1);
					Pending.Enqueue(text2);
					if (!loggedB75)
					{
						loggedB75 = true;
						MLog.Info("[URT] b75 live keeps up with the screen. Queue 24/frame, short labels, latin, no AUTO pause.");
					}
					TryStart();
				}
			}
		}

		private static bool NeedsLive(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			return HasForeignRun(text) || LanguageDetector.IsHalfTranslated(text, text2) || LanguageDetector.HasResidualEnglish(text, text2) || LooksLikeUntranslatedLatin(text, text2);
		}

		private static bool IsForeignLetter(char c)
		{
			return LanguageDetector.IsForeignScriptChar(c, UniversalRimTranslatorSettings.PlayerTargetLanguage());
		}

		public static bool Nudge(string text)
		{
			if (!userEnabled || paused)
			{
				return false;
			}
			if (UrtLive2.Engaged)
			{
				return UrtLive2.Enqueue(text);
			}
			if (Current.ProgramState != ProgramState.Playing)
			{
				return false;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = text.Trim();
			string text3 = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			if (!HasForeignRun(text2) && !LanguageDetector.IsHalfTranslated(text2, text3) && !LanguageDetector.HasResidualEnglish(text2, text3) && !LooksLikeUntranslatedLatin(text2, text3))
			{
				return false;
			}
			int tickCount = Environment.TickCount;
			if (nextTry.TryGetValue(text2, out var value) && tickCount - value < 4000)
			{
				return false;
			}
			Closed.TryRemove(text2, out var value2);
			Considered.TryRemove(text2, out value2);
			Attempts.TryRemove(text2, out var _);
			bool flag = Enqueue(text2);
			if (flag)
			{
				nextTry[text2] = tickCount;
			}
			return flag;
		}

		private static bool LooksLikeUntranslatedLatin(string text, string target)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = text.Trim();
			string a = LanguageCodeMapper.Canonical(target);
			if (string.Equals(a, "English", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (text2.Length < 2 || text2.Length > 8000)
			{
				return false;
			}
			if (text2.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text2.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (UrtOptEngine.LooksNonTranslatable(text2))
			{
				return false;
			}
			if (string.Equals(a, "Czech", StringComparison.OrdinalIgnoreCase) && LanguageDetector.LooksLikeCzech(text2))
			{
				return false;
			}
			int num = 0;
			int num2 = 0;
			bool flag = false;
			string text3 = text2;
			foreach (char c in text3)
			{
				if (char.IsLetter(c))
				{
					if (c > 'ɏ')
					{
						return true;
					}
					num++;
					if (!flag)
					{
						num2++;
						flag = true;
					}
				}
				else
				{
					flag = false;
				}
			}
			return num >= 3 && num2 >= 1;
		}

		public static bool Enqueue(string text)
		{
			if (!userEnabled || paused)
			{
				return false;
			}
			if (UrtNonText.LooksLikeNonText(text))
			{
				return false;
			}
			if (UrtLive2.Engaged)
			{
				return UrtLive2.Enqueue(text);
			}
			if (Current.ProgramState != ProgramState.Playing)
			{
				return false;
			}
			if (string.IsNullOrWhiteSpace(text) || text.Length > 8000 || text.Length < 2)
			{
				return false;
			}
			if (text.IndexOf("Universal RimTranslator", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("LIVE  [", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("URT_", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
			string text2 = text.Trim();
			if (Considered.ContainsKey(text2) || Closed.ContainsKey(text2))
			{
				return false;
			}
			if (nextTry.TryGetValue(text2, out var value) && value - Environment.TickCount > 0)
			{
				return false;
			}
			int frameCount = Time.frameCount;
			if (frameCount != enqueueFrame)
			{
				enqueueFrame = frameCount;
				enqueueThisFrame = 0;
			}
			if (enqueueThisFrame >= 24)
			{
				return false;
			}
			if (Pending.Count > 800)
			{
				return false;
			}
			if (!Attempts.TryAdd(text2, 0))
			{
				Considered.TryAdd(text2, 1);
				return false;
			}
			enqueueThisFrame++;
			Considered.TryAdd(text2, 1);
			Pending.Enqueue(text2);
			TryStart();
			return true;
		}

		public static void ForgetPending(string text)
		{
			if (!string.IsNullOrWhiteSpace(text))
			{
				Close(text.Trim(), persist: true);
			}
		}

		public static void CloseMany(IEnumerable<string> texts)
		{
			if (texts == null)
			{
				return;
			}
			foreach (string text in texts)
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					Close(text.Trim(), persist: true);
				}
			}
		}

		public static void TryStart()
		{
			if (paused)
			{
				return;
			}
			while (Pending.Count > 0)
			{
				int num = Interlocked.Increment(ref running);
				if (num > 4)
				{
					Interlocked.Decrement(ref running);
					break;
				}
				ThreadPool.QueueUserWorkItem(delegate
				{
					WorkerOne();
				});
			}
		}

		private static void RetryLater(string text, int delayMs, int maxAttempts)
		{
			if (!string.IsNullOrWhiteSpace(text))
			{
				Attempts.TryGetValue(text, out var value);
				value++;
				Attempts[text] = value;
				if (value < maxAttempts)
				{
					ScheduleRetry(text, Math.Max(2000, delayMs / 2) * value);
					return;
				}
				Closed.TryRemove(text, out var value2);
				Considered.TryRemove(text, out value2);
				Attempts.TryRemove(text, out var _);
				nextTry[text] = Environment.TickCount + Math.Max(1000, delayMs);
				RuntimeTextTranslator.ReleaseMiss(text);
			}
		}

		private static void WorkerOne()
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			bool flag = false;
			try
			{
				if (!Pending.TryDequeue(out var result))
				{
					return;
				}
				flag = true;
				if (triedThisPass == null)
				{
					triedThisPass = new HashSet<string>(StringComparer.Ordinal);
				}
				triedThisPass.Clear();
				try
				{
					EnsureSkipLoaded();
					if (Closed.ContainsKey(result) || Held.ContainsKey(result))
					{
						UrtLiveDiag.Outcome(Held.ContainsKey(result) ? "held" : "alreadyClosed");
						if (Held.ContainsKey(result))
						{
							ScheduleRetry(result, 4000);
						}
						return;
					}
					if (UrtOptEngine.LooksNonTranslatable(result) || LooksUntranslatable(result))
					{
						UrtLiveDiag.Outcome("untranslatable");
						Close(result, persist: false);
						return;
					}
					string text = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
					if (string.IsNullOrWhiteSpace(text))
					{
						text = LanguageCodeMapper.Canonical("English");
					}
					if (!HasCjk(result) && !LanguageDetector.HasLeftoverLatin(result) && LooksLikeTargetAlready(result) && !LanguageDetector.IsHalfTranslated(result, text) && !LanguageDetector.HasResidualEnglish(result, text) && result.IndexOf("<color", StringComparison.OrdinalIgnoreCase) < 0)
					{
						UrtLiveDiag.Outcome("alreadyTarget");
						Close(result, persist: false);
						return;
					}
					if (!loggedB74)
					{
						loggedB74 = true;
						MLog.Info("[URT] b74 live translates visible text instead of closing it.");
					}
					if (UrtPackLibrary.Try(result, "", "", "", "", text, out var translation) && IsGoodResult(result, translation))
					{
						UrtLiveDiag.Outcome("packHit");
						Completed.Enqueue(new KeyValuePair<string, string>(result, translation.Trim()));
						Close(result, persist: false);
						Interlocked.Increment(ref done);
						return;
					}
					if (UrtOptEngine.TryRam(result, text, out var translation2) && IsGoodResult(result, translation2))
					{
						UrtLiveDiag.Outcome("ramHit");
						Completed.Enqueue(new KeyValuePair<string, string>(result, translation2.Trim()));
						Close(result, persist: false);
						Interlocked.Increment(ref done);
						return;
					}
					if (HasForeignRun(result))
					{
						string text2 = TranslateForeignRuns(result, laneRr);
						if (!string.IsNullOrEmpty(text2) && !string.Equals(text2, result, StringComparison.Ordinal) && !HasForeignRun(text2) && !LanguageDetector.IsHalfTranslated(text2, text) && !LanguageDetector.HasResidualEnglish(text2, text))
						{
							UrtLiveDiag.Outcome("foreignRunsOk");
							Completed.Enqueue(new KeyValuePair<string, string>(result, text2.Trim()));
							Close(result, persist: false);
							Interlocked.Increment(ref done);
							return;
						}
						if (!string.IsNullOrEmpty(text2) && !string.Equals(text2, result, StringComparison.Ordinal))
						{
							Attempts.TryGetValue(result, out var value);
							value++;
							Attempts[result] = value;
							UrtLiveDiag.Outcome("foreignRunsRetry");
							if (value < 3)
							{
								ScheduleRetry(result, 4000 * value);
							}
							else
							{
								RetryLater(result, 10000, 4);
							}
							return;
						}
					}
					bool flag2 = false;
					Mask(result, out var masked, out var tokens);
					int lane = Interlocked.Increment(ref laneRr);
					string text3 = AwaitInFlight(result, masked, lane);
					if (!string.IsNullOrWhiteSpace(text3))
					{
						text3 = LanguageDetector.RepairMarkup(UrtGlossary.After(Unmask(text3, tokens)));
					}
					if (!string.IsNullOrWhiteSpace(text3) && !BrokenGrammar(text3) && TranslationBatchManager.IsUsableProviderTranslation(result, text3))
					{
						UrtLiveDiag.Outcome("translated");
						Completed.Enqueue(new KeyValuePair<string, string>(result, text3.Trim()));
						Close(result, persist: false);
						Interlocked.Increment(ref done);
					}
					else
					{
						if (string.IsNullOrWhiteSpace(text3))
						{
							UrtLiveDiag.Outcome("noProviderResult");
						}
						else if (BrokenGrammar(text3))
						{
							UrtLiveDiag.Outcome("brokenGrammar");
						}
						else
						{
							UrtLiveDiag.Outcome("unusableResult");
						}
						Attempts.TryGetValue(result, out var value2);
						value2++;
						Attempts[result] = value2;
						if (value2 < 4)
						{
							ScheduleRetry(result, 5000 * value2);
						}
						else if (StillNeedsWork(result, text) || LanguageDetector.HasLeftoverLatin(result) || (result != null && result.IndexOf("<color", StringComparison.OrdinalIgnoreCase) >= 0))
						{
							Closed.TryRemove(result, out var value3);
							Considered.TryRemove(result, out value3);
							Attempts.TryRemove(result, out var _);
							nextTry[result] = Environment.TickCount + 20000;
							RuntimeTextTranslator.ReleaseMiss(result);
							UrtLiveDiag.Outcome("gaveUpFor20s");
						}
						else
						{
							UrtLiveDiag.Outcome("closedNoRewrite");
							Close(result, persist: false);
							Interlocked.Increment(ref closedCount);
							if (closedCount <= 8)
							{
								MLog.Info("[URT] Live no rewrite: \"" + ((result.Length > 80) ? result.Substring(0, 80) : result) + "\"");
							}
						}
					}
				}
				catch (Exception ex)
				{
					UrtLiveDiag.Outcome("workerError");
					if (providerDiagLog < 12)
					{
						Interlocked.Increment(ref providerDiagLog);
						MLog.Info("[URT-LIVE] worker error -> retry: " + ex.GetType().Name + " " + ex.Message + " @ " + ((ex.StackTrace == null) ? "" : ex.StackTrace.Split(new char[1] { '\n' })[0].Trim()));
					}
					RetryLater(result, 10000, 4);
				}
			}
			finally
			{
				if (flag)
				{
					UrtLiveDiag.Worker(stopwatch.ElapsedMilliseconds);
				}
				Interlocked.Decrement(ref running);
				if (Pending.Count > 0)
				{
					TryStart();
				}
			}
			int num = done;
			if (num > lastLog && num - lastLog >= 5)
			{
				lastLog = num;
				MLog.Info("[Universal RimTranslator] Live translated " + num + ", closed " + closedCount + ".");
			}
		}

		private static int ScriptClass(char c)
		{
			if ((c >= '\u3040' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ'))
			{
				return 2;
			}
			if ((c >= '㐀' && c <= '\u9fff') || (c >= '豈' && c <= '\ufaff'))
			{
				return 3;
			}
			if ((c >= '가' && c <= '\ud7af') || (c >= 'ᄀ' && c <= 'ᇿ'))
			{
				return 4;
			}
			if (c >= 'Ѐ' && c <= 'ӿ')
			{
				return 5;
			}
			if (c >= '\u0600' && c <= 'ۿ')
			{
				return 6;
			}
			if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '\'')
			{
				return 1;
			}
			return 0;
		}

		private static string TranslateMixedScripts(string text, int lane)
		{
			if (string.IsNullOrEmpty(text) || text.Length < 4)
			{
				return null;
			}
			List<string> list = new List<string>();
			List<int> list2 = new List<int>();
			StringBuilder stringBuilder = new StringBuilder();
			int num = -2;
			for (int i = 0; i < text.Length; i++)
			{
				int num2 = ScriptClass(text[i]);
				int num3 = ((num2 == 0) ? num : num2);
				if (num3 != num && stringBuilder.Length > 0)
				{
					list.Add(stringBuilder.ToString());
					list2.Add((num >= 0) ? num : 0);
					stringBuilder.Length = 0;
				}
				if (num2 != 0)
				{
					num = num2;
				}
				stringBuilder.Append(text[i]);
			}
			if (stringBuilder.Length > 0)
			{
				list.Add(stringBuilder.ToString());
				list2.Add((num >= 0) ? num : 0);
			}
			HashSet<int> hashSet = new HashSet<int>();
			for (int j = 0; j < list2.Count; j++)
			{
				if (list2[j] >= 1)
				{
					hashSet.Add(list2[j]);
				}
			}
			if (hashSet.Count < 2)
			{
				return null;
			}
			StringBuilder stringBuilder2 = new StringBuilder();
			bool flag = false;
			for (int k = 0; k < list.Count; k++)
			{
				string text2 = list[k];
				int num4 = list2[k];
				if (num4 <= 1 || text2.Trim().Length < 2)
				{
					stringBuilder2.Append(text2);
					continue;
				}
				string text3 = TranslateOneLane(text2, lane);
				if (IsGoodResult(text2, text3))
				{
					stringBuilder2.Append(text3);
					flag = true;
				}
				else
				{
					stringBuilder2.Append(text2);
				}
			}
			return flag ? stringBuilder2.ToString() : null;
		}

		private static string TranslateForeignRuns(string text, int lane)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			string text2 = text;
			bool flag = false;
			StringBuilder stringBuilder = new StringBuilder();
			List<string> list = new List<string>();
			for (int i = 0; i <= text.Length; i++)
			{
				if (i < text.Length && IsForeignLetter(text[i]))
				{
					stringBuilder.Append(text[i]);
					continue;
				}
				if (stringBuilder.Length >= 2)
				{
					list.Add(stringBuilder.ToString());
				}
				stringBuilder.Length = 0;
			}
			for (int j = 0; j < list.Count; j++)
			{
				string text3 = list[j];
				string text4 = TranslateOneLane(text3, lane);
				if (!string.IsNullOrWhiteSpace(text4))
				{
					text4 = UrtGlossary.After(text4.Trim());
					if (IsGoodResult(text3, text4))
					{
						Completed.Enqueue(new KeyValuePair<string, string>(text3, text4));
						text2 = text2.Replace(text3, text4);
						flag = true;
					}
				}
			}
			return flag ? text2 : null;
		}

		private static string TranslateOneLane(string text, int lane)
		{
			string text2 = TargetLangCode();
			if (string.IsNullOrEmpty(text2))
			{
				text2 = LanguageCodeMapper.ToProviderCode(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			}
			if (ReadSettingsBool("enableAzure", fallback: true) && !string.IsNullOrWhiteSpace(ReadSettingsString("azureApiKey")))
			{
				string text3 = TranslateAzure(text, text2);
				if (IsGoodResult(text, text3))
				{
					return text3;
				}
			}
			string text4 = TranslateEdgeLive(text, text2);
			if (IsGoodResult(text, text4))
			{
				return text4;
			}
			if (ReadSettingsBool("enableYandex", fallback: true))
			{
				string text5 = TranslateYandex(text, text2);
				if (IsGoodResult(text, text5))
				{
					if (providerDiagLog < 20)
					{
						Interlocked.Increment(ref providerDiagLog);
						MLog.Info("[URT-LIVE] Yandex returned a usable translation.");
					}
					return text5;
				}
			}
			if (ReadSettingsBool("enableGoogleWeb", fallback: false))
			{
				string text6 = TranslateGoogleAuto(text, text2);
				if (IsGoodResult(text, text6))
				{
					return text6;
				}
			}
			if (ReadSettingsBool("enableLingva", fallback: false))
			{
				string text7 = TranslateLingva(text, text2);
				if (IsGoodResult(text, text7))
				{
					return text7;
				}
			}
			return null;
		}

		private static string RaceStableProviders(string text)
		{
			string tl = TargetLangCode();
			if (string.IsNullOrEmpty(tl))
			{
				tl = LanguageCodeMapper.ToProviderCode(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			}
			string azure = null;
			string edge = null;
			List<Task> list = new List<Task>();
			if (ReadSettingsBool("enableAzure", fallback: true) && !string.IsNullOrWhiteSpace(ReadSettingsString("azureApiKey")))
			{
				list.Add(Task.Run(delegate
				{
					ResetTriedThisPass();
					azure = TranslateAzure(text, tl);
				}));
			}
			list.Add(Task.Run(delegate
			{
				ResetTriedThisPass();
				edge = TranslateEdgeLive(text, tl);
			}));
			try
			{
				Task.WaitAll(list.ToArray(), 8000);
			}
			catch
			{
			}
			if (IsGoodResult(text, azure))
			{
				return azure;
			}
			if (IsGoodResult(text, edge))
			{
				return edge;
			}
			return null;
		}

		private static string CollapseWs(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}
			string text = value.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
			while (text.Contains("  "))
			{
				text = text.Replace("  ", " ");
			}
			return text;
		}

		private static string AwaitInFlight(string original, string masked, int lane)
		{
			string key = CollapseWs(original ?? "");
			TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
			Task<string> orAdd = InFlight.GetOrAdd(key, (string _) => tcs.Task);
			if (orAdd != tcs.Task)
			{
				try
				{
					return orAdd.Result;
				}
				catch
				{
					return null;
				}
			}
			string result = null;
			try
			{
				result = TranslateNatural(UrtGlossary.Before(masked), lane);
				tcs.TrySetResult(result);
			}
			catch
			{
				tcs.TrySetResult(null);
			}
			finally
			{
				InFlight.TryRemove(key, out var _);
			}
			return result;
		}

		private static string TranslateNatural(string text, int lane = 0)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			string targetLanguage = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			bool flag = HasForeignRun(text) || LanguageDetector.IsMixedLanguage(text, targetLanguage);
			string text2 = RaceStableProviders(text);
			if (!string.IsNullOrEmpty(text2))
			{
				return text2;
			}
			string tl = TargetLangCode();
			bool flag2 = LooksAdultUi(text);
			string text3 = ReadSettingsString("googleApiKey");
			int num = Math.Abs(lane) % 4;
			if (flag2 && ReadSettingsBool("enableYandex", fallback: true))
			{
				string text4 = TranslateYandex(text, tl);
				if (IsGoodResult(text, text4))
				{
					return text4;
				}
			}
			if (num == 1 && !flag2)
			{
				string text5 = TranslateGoogleAuto(text, tl);
				if (IsGoodResult(text, text5))
				{
					return text5;
				}
			}
			if (num == 2 && ReadSettingsBool("enableLingva", fallback: false))
			{
				string text6 = TranslateLingva(text, tl);
				if (IsGoodResult(text, text6))
				{
					return text6;
				}
			}
			if (num == 3 && ReadSettingsBool("enableGemini", fallback: false))
			{
				string text7 = TranslateGeminiLive(text, tl);
				if (IsGoodResult(text, text7))
				{
					return text7;
				}
			}
			if (num == 0 && ReadSettingsBool("enableMyMemory", fallback: true))
			{
				string text8 = TranslateMyMemory(text, tl);
				if (IsGoodResult(text, text8))
				{
					return text8;
				}
			}
			if (ReadSettingsBool("enableGoogleCloud", fallback: true) && !string.IsNullOrWhiteSpace(text3))
			{
				string text9 = TranslateGoogleCloud(text, tl, text3);
				if (IsGoodResult(text, text9))
				{
					return text9;
				}
			}
			if (!flag2)
			{
				string text10 = TranslateGoogleAuto(text, tl);
				if (IsGoodResult(text, text10))
				{
					return text10;
				}
			}
			if (ReadSettingsBool("enableLingva", fallback: false))
			{
				string text11 = TranslateLingva(text, tl);
				if (IsGoodResult(text, text11))
				{
					return text11;
				}
			}
			if (ReadSettingsBool("enableYandex", fallback: true))
			{
				string text12 = TranslateYandex(text, tl);
				if (IsGoodResult(text, text12))
				{
					return text12;
				}
			}
			if (flag)
			{
				string text13 = TranslateToEnglishIntermediate(text);
				if (!string.IsNullOrEmpty(text13) && !string.Equals(CollapseWs(text13), CollapseWs(text), StringComparison.OrdinalIgnoreCase))
				{
					string text14 = TranslateEnglishToTarget(text13);
					if (!string.IsNullOrEmpty(text14) && IsGoodResult(text, text14))
					{
						return text14;
					}
				}
				string text15 = TranslateMixedScripts(text, lane);
				if (!string.IsNullOrEmpty(text15) && IsGoodResult(text, text15))
				{
					return text15;
				}
			}
			if (ReadSettingsBool("enableMyMemory", fallback: true))
			{
				string text16 = TranslateMyMemory(text, tl);
				if (IsGoodResult(text, text16))
				{
					return text16;
				}
			}
			if (Interlocked.CompareExchange(ref customBusy, 1, 0) != 0)
			{
				UrtLiveDiag.Outcome("customBusySkipped");
				return null;
			}
			string text17;
			try
			{
				text17 = TranslateCustomLive(text, tl);
			}
			finally
			{
				Interlocked.Exchange(ref customBusy, 0);
			}
			if (IsGoodResult(text, text17))
			{
				return text17;
			}
			return null;
		}

		private static string TranslateToEnglishIntermediate(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			try
			{
				if (ReadSettingsBool("enableAzure", fallback: true) && !string.IsNullOrWhiteSpace(ReadSettingsString("azureApiKey")))
				{
					string text2 = TranslateAzure(text, "en");
					if (IsUsableIntermediate(text, text2))
					{
						return text2;
					}
				}
				string text3 = TranslateEdgeLive(text, "en");
				if (IsUsableIntermediate(text, text3))
				{
					return text3;
				}
				if (ReadSettingsBool("enableYandex", fallback: true))
				{
					string text4 = TranslateYandex(text, "en");
					if (IsUsableIntermediate(text, text4))
					{
						return text4;
					}
				}
				if (ReadSettingsBool("enableGoogleWeb", fallback: false))
				{
					string text5 = TranslateGoogleAuto(text, "en");
					if (IsUsableIntermediate(text, text5))
					{
						return text5;
					}
				}
				string text6 = ReadSettingsString("googleApiKey");
				if (ReadSettingsBool("enableGoogleCloud", fallback: true) && !string.IsNullOrWhiteSpace(text6))
				{
					string text7 = TranslateGoogleCloud(text, "en", text6);
					if (IsUsableIntermediate(text, text7))
					{
						return text7;
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private static string TranslateEnglishToTarget(string english)
		{
			if (string.IsNullOrWhiteSpace(english))
			{
				return null;
			}
			string tl = TargetLangCode();
			try
			{
				if (ReadSettingsBool("enableAzure", fallback: true) && !string.IsNullOrWhiteSpace(ReadSettingsString("azureApiKey")))
				{
					string text = TranslateAzure(english, tl);
					if (IsGoodResult(english, text))
					{
						return text;
					}
				}
				string text2 = TranslateEdgeLive(english, tl);
				if (IsGoodResult(english, text2))
				{
					return text2;
				}
				if (ReadSettingsBool("enableYandex", fallback: true))
				{
					string text3 = TranslateYandex(english, tl);
					if (IsGoodResult(english, text3))
					{
						return text3;
					}
				}
				string text4 = ReadSettingsString("googleApiKey");
				if (ReadSettingsBool("enableGoogleCloud", fallback: true) && !string.IsNullOrWhiteSpace(text4))
				{
					string text5 = TranslateGoogleCloud(english, tl, text4);
					if (IsGoodResult(english, text5))
					{
						return text5;
					}
				}
				string text6 = TranslateGoogleAuto(english, tl);
				if (IsGoodResult(english, text6))
				{
					return text6;
				}
			}
			catch
			{
			}
			return null;
		}

		private static bool IsUsableIntermediate(string source, string translated)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			if (LooksLikeProviderError(translated))
			{
				return false;
			}
			if (LanguageDetector.HasBrokenMarkup(translated))
			{
				return false;
			}
			if (string.Equals(CollapseWs(source), CollapseWs(translated), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			return translated.Trim().Length >= 2;
		}

		private static void ResetTriedThisPass()
		{
			if (triedThisPass == null)
			{
				triedThisPass = new HashSet<string>(StringComparer.Ordinal);
			}
			else
			{
				triedThisPass.Clear();
			}
		}

		private static bool FirstTry(string provider, string text, string tl)
		{
			if (triedThisPass == null)
			{
				triedThisPass = new HashSet<string>(StringComparer.Ordinal);
			}
			return triedThisPass.Add(provider + "|" + tl + "|" + text);
		}

		private static bool ProviderUsable(string provider)
		{
			return !UrtOptEngine.IsCoolingDown(provider) && !UrtOptEngine.CircuitOpen(provider);
		}

		private static string LiveHttp(string provider, HttpMethod method, string url, HttpContent content, string headerName = null, string headerValue = null)
		{
			//IL_0019: Unknown result type (might be due to invalid IL or missing references)
			//IL_0020: Expected O, but got Unknown
			Stopwatch stopwatch = Stopwatch.StartNew();
			int num = 0;
			bool flag = false;
			try
			{
				using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(12000);
				HttpRequestMessage val = new HttpRequestMessage(method, url);
				try
				{
					if (content != null)
					{
						val.Content = content;
					}
					if (!string.IsNullOrEmpty(headerName))
					{
						((HttpHeaders)val.Headers).TryAddWithoutValidation(headerName, headerValue ?? "");
					}
					HttpResponseMessage result = ((HttpMessageInvoker)ExtraHttp.Client).SendAsync(val, cancellationTokenSource.Token).GetAwaiter().GetResult();
					try
					{
						num = (int)result.StatusCode;
						string result2 = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();
						if (num == 429)
						{
							UrtOptEngine.CoolDown(provider, 30);
						}
						flag = result.IsSuccessStatusCode;
						return flag ? result2 : null;
					}
					finally
					{
						((IDisposable)result)?.Dispose();
					}
				}
				finally
				{
					((IDisposable)val)?.Dispose();
				}
			}
			catch
			{
				return null;
			}
			finally
			{
				UrtOptEngine.Record(provider, flag, (int)stopwatch.ElapsedMilliseconds, num);
				UrtLiveDiag.Http(provider, flag, stopwatch.ElapsedMilliseconds, num, 12000);
			}
		}

		private static string TranslateEdgeLive(string text, string tl)
		{
			string provider = "Microsoft Edge";
			if (!ReadSettingsBool("enableMicrosoftEdge", fallback: true) || !ProviderUsable(provider) || !FirstTry(provider, text, tl))
			{
				return null;
			}
			return RunProviderSync(new MicrosoftEdgeTranslateProvider(), text, tl, new TranslationProviderSettings(), 12000);
		}

		private static string TranslateCustomLive(string text, string tl)
		{
			string provider = "Custom endpoint";
			if (!ReadSettingsBool("enableCustomEndpoint", fallback: false) || !ProviderUsable(provider) || !FirstTry(provider, text, tl))
			{
				return null;
			}
			TranslationProviderSettings settings = new TranslationProviderSettings
			{
				EnableCustomEndpoint = true,
				CustomEndpointType = (ReadSettingsString("customEndpointType") ?? "LibreTranslate"),
				CustomEndpointUrl = (ReadSettingsString("customEndpointUrl") ?? ""),
				CustomEndpointModel = (ReadSettingsString("customEndpointModel") ?? ""),
				CustomEndpointKey = (ReadSettingsString("customEndpointKey") ?? "")
			};
			return RunProviderSync(new CustomEndpointTranslateProvider(), text, tl, settings, 60000);
		}

		private static string RunProviderSync(IUniversalTranslationProvider provider, string text, string tl, TranslationProviderSettings settings, int timeoutMs)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			TranslationProviderResult translationProviderResult = null;
			try
			{
				using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(timeoutMs);
				translationProviderResult = provider.TranslateBatchAsync(new List<string> { text }, "Auto", tl, settings, cancellationTokenSource.Token).GetAwaiter().GetResult();
			}
			catch
			{
				translationProviderResult = null;
			}
			UrtOptEngine.Record(provider.Name, translationProviderResult?.Success ?? false, (int)stopwatch.ElapsedMilliseconds, translationProviderResult?.StatusCode ?? 0);
			UrtLiveDiag.Http(provider.Name, translationProviderResult?.Success ?? false, stopwatch.ElapsedMilliseconds, translationProviderResult?.StatusCode ?? 0, timeoutMs);
			if (translationProviderResult == null || !translationProviderResult.Success || translationProviderResult.Translations == null || translationProviderResult.Translations.Count == 0)
			{
				return null;
			}
			string text2 = translationProviderResult.Translations[0];
			return string.IsNullOrWhiteSpace(text2) ? null : text2;
		}

		private static string TranslateLingva(string text, string tl)
		{
			if (!ReadSettingsBool("enableLingva", fallback: false) || !ProviderUsable("Lingva") || !FirstTry("Lingva", text, tl))
			{
				return null;
			}
			string text2 = ReadSettingsString("lingvaEndpoint");
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = new TranslationProviderSettings().LingvaEndpoint;
			}
			string text3 = LingvaCode(tl);
			string url = text2.Trim().TrimEnd(new char[1] { '/' }) + "/api/v1/auto/" + text3 + "/" + Uri.EscapeDataString(text ?? "");
			string json = LiveHttp("Lingva", HttpMethod.Get, url, null);
			return ProviderJsonHelper.ExtractFirstString(json, "translation");
		}

		private static string LingvaCode(string tl)
		{
			string text = (tl ?? "").ToLowerInvariant();
			return text switch
			{
				"zh-tw" => "zh_HANT", 
				"zh-cn" => "zh", 
				"pt-pt" => "pt", 
				_ => text, 
			};
		}

		private static string TranslateGeminiLive(string text, string tl)
		{
			//IL_008f: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a4: Expected O, but got Unknown
			string text2 = ReadSettingsString("geminiApiKey");
			if (!ReadSettingsBool("enableGemini", fallback: false) || string.IsNullOrWhiteSpace(text2) || !ProviderUsable("Gemini") || !FirstTry("Gemini", text, tl))
			{
				return null;
			}
			string text3 = "Translate to " + tl + ". Keep placeholders like {0}, [TAG] and <color> unchanged. Return only the translation.\n" + text;
			string text4 = "{\"contents\":[{\"parts\":[{\"text\":\"" + ProviderJsonHelper.JsonEscape(text3) + "\"}]}]}";
			string json = LiveHttp("Gemini", HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", (HttpContent)new StringContent(text4, Encoding.UTF8, "application/json"), "x-goog-api-key", text2.Trim());
			List<string> list = ProviderJsonHelper.ExtractStringValues(json, "text");
			return (list.Count == 0) ? null : string.Join("", list.ToArray()).Trim();
		}

		private static string TranslateMyMemory(string text, string tl)
		{
			if (!ReadSettingsBool("enableMyMemory", fallback: true) || !ProviderUsable("MyMemory") || !FirstTry("MyMemory", text, tl))
			{
				return null;
			}
			string text2 = LanguageDetector.DetectEmbeddedSourceLanguage(text);
			string text3 = (string.IsNullOrEmpty(text2) ? "en" : LanguageCodeMapper.ToProviderCode(text2));
			if (string.Equals(text3, tl, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string text4 = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString(text3 + "|" + tl);
			string text5 = ReadSettingsString("myMemoryEmail");
			if (!string.IsNullOrWhiteSpace(text5))
			{
				text4 = text4 + "&de=" + Uri.EscapeDataString(text5.Trim());
			}
			string json = LiveHttp("MyMemory", HttpMethod.Get, text4, null);
			string text6 = ProviderJsonHelper.ExtractFirstString(json, "translatedText");
			if (!string.IsNullOrEmpty(text6) && text6.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				UrtOptEngine.CoolDown("MyMemory", 3600);
				return null;
			}
			return string.IsNullOrEmpty(text6) ? null : WebUtility.HtmlDecode(text6);
		}

		private static bool LooksLikeProviderError(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			string text2 = text.ToUpperInvariant();
			return text2.IndexOf("INVALID SOURCE LANGUAGE", StringComparison.Ordinal) >= 0 || text2.IndexOf("LANGPAIR", StringComparison.Ordinal) >= 0 || text2.IndexOf("MYMEMORY", StringComparison.Ordinal) >= 0 || text2.IndexOf("PLEASE SPECIFY", StringComparison.Ordinal) >= 0 || text2.IndexOf("QUERY LENGTH LIMIT", StringComparison.Ordinal) >= 0 || text2.IndexOf("INVALID API", StringComparison.Ordinal) >= 0;
		}

		private static bool IsGoodResult(string source, string translated)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			if (LooksLikeProviderError(translated))
			{
				return UrtLiveDiag.Reject("providerError");
			}
			translated = LanguageDetector.RepairMarkup(translated);
			if (LanguageDetector.HasBrokenMarkup(translated))
			{
				return UrtLiveDiag.Reject("brokenMarkup");
			}
			string a = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			if (!string.Equals(a, "English", StringComparison.OrdinalIgnoreCase) && LanguageDetector.HasEnglishSentence(translated))
			{
				return UrtLiveDiag.Reject("englishSentence", source, translated);
			}
			if (!LooksUseful(translated))
			{
				return UrtLiveDiag.Reject("notUseful");
			}
			if (!LanguageDetector.IsTranslationComplete(source, translated, UniversalRimTranslatorSettings.PlayerTargetLanguage()))
			{
				return UrtLiveDiag.Reject("incomplete", source, translated);
			}
			if (HasForeignRun(translated))
			{
				return UrtLiveDiag.Reject("foreignScript");
			}
			if (UrtResultValidator.LooksSpliced(source, translated))
			{
				return UrtLiveDiag.Reject("spliced");
			}
			return true;
		}

		private static void Persist(string source, string translation)
		{
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod != null && mod.settings != null)
				{
					mod.settings.AcceptLiveTranslation(source, translation);
				}
			}
			catch
			{
			}
		}

		private static void TryStartPersistence()
		{
			if ((PersistencePending.IsEmpty && SkipWrites.IsEmpty) || Interlocked.CompareExchange(ref persistenceRunning, 1, 0) != 0)
			{
				return;
			}
			ThreadPool.QueueUserWorkItem(delegate
			{
				bool flag = false;
				try
				{
					while (true)
					{
						bool flag2 = false;
						if (PersistencePending.TryDequeue(out var result))
						{
							try
							{
								Persist(result.Key, result.Value);
							}
							catch (Exception ex)
							{
								MLog.Warn("[URT] Live cache persistence failed: " + ex.Message);
							}
							flag = true;
							flag2 = true;
						}
						else
						{
							List<string> list = new List<string>();
							string result2;
							while (SkipWrites.TryDequeue(out result2))
							{
								list.Add(result2);
							}
							if (list.Count > 0)
							{
								try
								{
									File.AppendAllLines(SkipPath(), list, Encoding.UTF8);
								}
								catch
								{
								}
								flag2 = true;
							}
							if (!flag2)
							{
								if (!flag)
								{
									break;
								}
								long num = Interlocked.Read(ref persistenceLastQueuedTicks);
								if (DateTime.UtcNow.Ticks - num >= TimeSpan.FromSeconds(2.0).Ticks)
								{
									break;
								}
								Thread.Sleep(100);
							}
						}
					}
					if (flag)
					{
						FlushPendingLiveCache();
					}
				}
				finally
				{
					Interlocked.Exchange(ref persistenceRunning, 0);
					if (!PersistencePending.IsEmpty || !SkipWrites.IsEmpty)
					{
						TryStartPersistence();
					}
				}
			});
		}

		private static void FlushPendingLiveCache()
		{
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod != null && mod.settings != null)
				{
					mod.settings.FlushLiveCache();
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] Live cache flush failed: " + ex.Message);
			}
		}

		private static void Mask(string text, out string masked, out List<string> tokens)
		{
			List<string> found = new List<string>();
			masked = Token.Replace(text, delegate(Match m)
			{
				found.Add(m.Value);
				return "⟦" + (found.Count - 1) + "⟧";
			});
			tokens = found;
		}

		private static string Unmask(string text, List<string> tokens)
		{
			if (tokens == null || tokens.Count == 0)
			{
				return text;
			}
			for (int i = 0; i < tokens.Count; i++)
			{
				text = text.Replace("⟦" + i + "⟧", tokens[i]);
			}
			return text;
		}

		private static bool HasCjk(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			foreach (char c in text)
			{
				if ((c >= '가' && c <= '힣') || (c >= '\u3040' && c <= 'ヿ') || (c >= '\u3100' && c <= 'ㄯ') || (c >= '㐀' && c <= '\u9fff') || (c >= '豈' && c <= '\ufaff'))
				{
					return true;
				}
			}
			return false;
		}

		private static string ExtractCjkRuns(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			StringBuilder stringBuilder = new StringBuilder();
			foreach (char c in text)
			{
				if ((c >= '가' && c <= '힣') || (c >= '\u3040' && c <= 'ヿ') || (c >= '\u3100' && c <= 'ㄯ') || (c >= '㐀' && c <= '\u9fff') || (c >= '豈' && c <= '\ufaff'))
				{
					stringBuilder.Append(c);
				}
			}
			return stringBuilder.ToString();
		}

		private static bool LooksLikeQuestGrammar(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			if (text.StartsWith("ERR:", StringComparison.Ordinal))
			{
				return true;
			}
			if (text.IndexOf("nameDef", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (text.IndexOf("jménoDef", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (text.IndexOf('[') < 0)
			{
				return false;
			}
			return text.IndexOf("_name", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("PAWN_", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("asker_", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("factionOpponent", StringComparison.OrdinalIgnoreCase) >= 0 || text.Contains("[Betrayal") || text.Contains("[otherMembers") || text.Contains("[map_");
		}

		private static bool LooksUntranslatable(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return true;
			}
			if (Ident.IsMatch(text))
			{
				return true;
			}
			if (text.IndexOf('/') >= 0 && text.IndexOf('.') >= 0 && text.IndexOf(' ') < 0)
			{
				return true;
			}
			if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			int num = 0;
			foreach (char c in text)
			{
				if (char.IsLetter(c))
				{
					num++;
				}
			}
			if (HasCjk(text) && num >= 2)
			{
				return false;
			}
			if (num >= 3 && num <= 28 && text.IndexOf('_') < 0 && text.IndexOf('/') < 0 && text.IndexOf('.') < 0)
			{
				return false;
			}
			return num < 3;
		}

		private static bool LooksLikeModTitle(string text)
		{
			if (string.IsNullOrEmpty(text) || text.Length > 56 || text.IndexOf('\n') >= 0)
			{
				return false;
			}
			try
			{
				List<ModContentPack> runningModsListForReading = LoadedModManager.RunningModsListForReading;
				if (runningModsListForReading == null)
				{
					return false;
				}
				for (int i = 0; i < runningModsListForReading.Count; i++)
				{
					string text2 = ((runningModsListForReading[i] != null) ? runningModsListForReading[i].Name : null);
					if (!string.IsNullOrEmpty(text2) && string.Equals(text2.Trim(), text, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			catch
			{
			}
			return false;
		}

		private static bool TrySplitBilingual(string text, out string scriptPart, out string latinPart)
		{
			scriptPart = null;
			latinPart = null;
			if (string.IsNullOrEmpty(text) || text.Length < 10)
			{
				return false;
			}
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = new StringBuilder();
			int num = 0;
			int num2 = 0;
			foreach (char c in text)
			{
				int num3 = c;
				if ((num3 >= 44032 && num3 <= 55203) || (num3 >= 12352 && num3 <= 12543) || (num3 >= 13312 && num3 <= 40959) || (num3 >= 1024 && num3 <= 1279))
				{
					stringBuilder.Append(c);
					num++;
					continue;
				}
				if (char.IsLetter(c) && num3 < 592)
				{
					stringBuilder2.Append(c);
					num2++;
					continue;
				}
				if (stringBuilder.Length > 0)
				{
					stringBuilder.Append(c);
				}
				if (stringBuilder2.Length > 0)
				{
					stringBuilder2.Append(c);
				}
			}
			if (num < 2 || num2 < 4)
			{
				return false;
			}
			scriptPart = stringBuilder.ToString().Trim();
			latinPart = stringBuilder2.ToString().Trim();
			return scriptPart.Length >= 2 && latinPart.Length >= 4;
		}

		private static bool StillNeedsWork(string text, string target)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			if (HasForeignRun(text))
			{
				return true;
			}
			if (LanguageDetector.HasResidualForeignScript(text, target))
			{
				return true;
			}
			if (LanguageDetector.HasLeftoverLatin(text))
			{
				return true;
			}
			return LanguageDetector.IsHalfTranslated(text, target);
		}

		private static bool LooksLikeTargetAlready(string text)
		{
			string text2 = TargetLangCode();
			if (text2 == "cs" || text2 == "sk")
			{
				char[] marks = new char[30]
				{
					'á', 'é', 'í', 'ó', 'ú', 'ů', 'ý', 'č', 'ď', 'ě',
					'ň', 'ř', 'š', 'ť', 'ž', 'Á', 'É', 'Í', 'Ó', 'Ú',
					'Ů', 'Ý', 'Č', 'Ď', 'Ě', 'Ň', 'Ř', 'Š', 'Ť', 'Ž'
				};
				return HasMany(text, marks);
			}
			switch (text2)
			{
			case "de":
				return HasMany(text, new char[7] { 'ä', 'ö', 'ü', 'ß', 'Ä', 'Ö', 'Ü' });
			case "fr":
				return HasMany(text, new char[9] { 'é', 'è', 'ê', 'à', 'ù', 'ç', 'ô', 'î', 'ï' });
			default:
				if (!(text2 == "pt"))
				{
					if (text2 == "pl")
					{
						return HasMany(text, new char[9] { 'ą', 'ć', 'ę', 'ł', 'ń', 'ó', 'ś', 'ź', 'ż' });
					}
					if (text2 == "ru" || text2 == "uk")
					{
						return HasScript(text, 1024, 1279);
					}
					if (text2 == "ja")
					{
						return HasScript(text, 12352, 12543) || HasScript(text, 19968, 40959);
					}
					if (text2 == "ko")
					{
						return HasScript(text, 44032, 55203);
					}
					if (text2.StartsWith("zh"))
					{
						return HasScript(text, 19968, 40959);
					}
					return text2 switch
					{
						"ar" => HasScript(text, 1536, 1791), 
						"th" => HasScript(text, 3584, 3711), 
						"el" => HasScript(text, 880, 1023), 
						_ => false, 
					};
				}
				goto case "es";
			case "es":
				return HasMany(text, new char[9] { 'á', 'é', 'í', 'ó', 'ú', 'ñ', 'ã', 'õ', 'ç' });
			}
		}

		private static bool HasMany(string text, char[] marks)
		{
			int num = 0;
			int num2 = 0;
			foreach (char c in text)
			{
				if (char.IsLetter(c))
				{
					num++;
					if (Array.IndexOf(marks, c) >= 0)
					{
						num2++;
					}
				}
			}
			return num >= 6 && num2 * 5 >= num;
		}

		private static bool HasScript(string text, int from, int to)
		{
			int num = 0;
			int num2 = 0;
			foreach (char c in text)
			{
				if (char.IsLetter(c))
				{
					num++;
					if (c >= from && c <= to)
					{
						num2++;
					}
				}
			}
			return num >= 4 && num2 * 3 >= num;
		}

		private static string SpliceEnglishRuns(string text, string target)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = new StringBuilder();
			bool flag = false;
			int i = 0;
			while (i <= text.Length)
			{
				if (i < text.Length && (char.IsLetter(text[i]) || text[i] == '\''))
				{
					int num = i;
					for (; i < text.Length && (char.IsLetter(text[i]) || text[i] == '\'' || text[i] == '-'); i++)
					{
					}
					string text2 = text.Substring(num, i - num);
					if (IsEnglishToken(text2))
					{
						if (stringBuilder2.Length > 0)
						{
							stringBuilder2.Append(' ');
						}
						stringBuilder2.Append(text2);
						while (i < text.Length && text[i] == ' ')
						{
							int j;
							for (j = i + 1; j < text.Length && text[j] == ' '; j++)
							{
							}
							if (j < text.Length && (char.IsLetter(text[j]) || char.IsDigit(text[j])))
							{
								int k;
								for (k = j; k < text.Length && (char.IsLetter(text[k]) || char.IsDigit(text[k]) || text[k] == '-'); k++)
								{
								}
								string text3 = text.Substring(j, k - j);
								if (IsEnglishToken(text3) || (text3.Length <= 2 && stringBuilder2.Length > 0))
								{
									stringBuilder2.Append(' ');
									stringBuilder2.Append(text3);
									i = k;
									continue;
								}
								break;
							}
							break;
						}
						continue;
					}
					if (stringBuilder2.Length > 0)
					{
						string value = TranslateOneRun(stringBuilder2.ToString());
						if (!string.IsNullOrEmpty(value))
						{
							stringBuilder.Append(value);
							flag = true;
						}
						else
						{
							stringBuilder.Append(stringBuilder2.ToString());
						}
						stringBuilder2.Length = 0;
					}
					stringBuilder.Append(text2);
					continue;
				}
				if (stringBuilder2.Length > 0 && (i >= text.Length || !char.IsWhiteSpace(text[i])))
				{
					string value2 = TranslateOneRun(stringBuilder2.ToString());
					if (!string.IsNullOrEmpty(value2))
					{
						stringBuilder.Append(value2);
						flag = true;
					}
					else
					{
						stringBuilder.Append(stringBuilder2.ToString());
					}
					stringBuilder2.Length = 0;
				}
				if (i < text.Length)
				{
					stringBuilder.Append(text[i]);
				}
				i++;
			}
			return flag ? stringBuilder.ToString() : null;
		}

		private static bool IsEnglishToken(string token)
		{
			if (string.IsNullOrEmpty(token))
			{
				return false;
			}
			if (LanguageDetector.HasResidualEnglish(token, UniversalRimTranslatorSettings.PlayerTargetLanguage()))
			{
				return true;
			}
			if (token.Length >= 5)
			{
				for (int i = 0; i < token.Length; i++)
				{
					if (token[i] > '\u007f')
					{
						return false;
					}
				}
				if (!LanguageDetector.LooksLikeTargetText(token, UniversalRimTranslatorSettings.PlayerTargetLanguage()))
				{
					return true;
				}
			}
			return false;
		}

		private static string TranslateOneRun(string run)
		{
			if (string.IsNullOrWhiteSpace(run) || run.Length < 4)
			{
				return null;
			}
			string text = TranslateNatural(run);
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			if (string.Equals(Normalize(text), Normalize(run), StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			if (LanguageDetector.IsHalfTranslated(text, UniversalRimTranslatorSettings.PlayerTargetLanguage()))
			{
				return null;
			}
			return text.Trim();
		}

		private static bool BrokenGrammar(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			int num = 0;
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] == '{')
				{
					num++;
				}
				else if (text[i] == '}')
				{
					num--;
				}
				if (num < 0)
				{
					return true;
				}
			}
			if (num != 0)
			{
				return true;
			}
			return Regex.IsMatch(text, "\\{[^}\\d]");
		}

		private static bool LooksMostlySameWords(string a, string b)
		{
			string text = Normalize(a).ToLowerInvariant();
			string text2 = Normalize(b).ToLowerInvariant();
			if (text.Length < 12 || text2.Length < 8)
			{
				return false;
			}
			string[] array = text.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (array.Length < 3)
			{
				return false;
			}
			int num = 0;
			for (int i = 0; i < array.Length; i++)
			{
				if (array[i].Length >= 4 && text2.IndexOf(array[i], StringComparison.Ordinal) >= 0)
				{
					num++;
				}
			}
			return num * 2 >= array.Length;
		}

		private static bool LooksAdultUi(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			string text2 = text.ToLowerInvariant();
			return text2.Contains("sex") || text2.Contains("rape") || text2.Contains("rjw") || text2.Contains("whore") || text2.Contains("brothel") || text2.Contains("lovin") || text2.Contains("penis") || text2.Contains("vagina") || text2.Contains("anus") || text2.Contains("breast") || text2.Contains("pregnant") || text2.Contains("womb") || text2.Contains("oral") || text2.Contains("anal") || text2.Contains("cum") || text2.Contains("genital") || text2.Contains("breed") || text2.Contains("comfort woman") || text2.Contains("nymph") || text2.Contains("masturb");
		}

		private static bool LooksUseful(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			if (text.IndexOf('<') >= 0 && text.IndexOf('>') < 0)
			{
				return false;
			}
			return true;
		}

		private static string Normalize(string value)
		{
			return (value ?? "").Trim().Replace("\r\n", "\n").Replace("\r", "\n");
		}

		private static string SkipPath()
		{
			return Path.Combine(GenFilePaths.ConfigFolderPath, "URT_skip_failed.txt");
		}

		private static void EnsureSkipLoaded()
		{
			if (Interlocked.CompareExchange(ref skipLoaded, 1, 0) != 0)
			{
				return;
			}
			try
			{
				string path = SkipPath();
				if (!File.Exists(path))
				{
					return;
				}
				string[] array = File.ReadAllLines(path);
				foreach (string text in array)
				{
					if (!string.IsNullOrWhiteSpace(text))
					{
						string text2 = text.Trim();
						Closed.TryAdd(text2, 1);
						if (text2.StartsWith("h:", StringComparison.Ordinal))
						{
							Closed.TryAdd(text2.Substring(2), 1);
						}
						else
						{
							Closed.TryAdd("h:" + UrtHash.Of(text2), 1);
						}
					}
				}
			}
			catch
			{
			}
		}

		private static void Close(string text, bool persist)
		{
			if (!string.IsNullOrWhiteSpace(text))
			{
				string text2 = UrtHash.Of(text);
				Closed[text] = 1;
				Closed["h:" + text2] = 1;
				if (persist)
				{
					SkipWrites.Enqueue("h:" + text2);
				}
			}
		}

		private static string TargetLangCode()
		{
			string text = ReadSettingsString("targetLanguage");
			if (string.IsNullOrWhiteSpace(text))
			{
				return "en";
			}
			string text2 = LanguageCodeMapper.ToProviderCode(LanguageCodeMapper.Canonical(text));
			return string.IsNullOrWhiteSpace(text2) ? "en" : text2;
		}

		private static object ReadSettingsValue(string field)
		{
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod == null || mod.settings == null)
				{
					return null;
				}
				FieldInfo orAdd = SettingsFields.GetOrAdd(field, (string f) => mod.settings.GetType().GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
				return (orAdd == null) ? null : orAdd.GetValue(mod.settings);
			}
			catch
			{
				return null;
			}
		}

		private static string ReadSettingsString(string field)
		{
			return ReadSettingsValue(field) as string;
		}

		private static bool ReadSettingsBool(string field, bool fallback)
		{
			object obj = ReadSettingsValue(field);
			return (obj is bool) ? ((bool)obj) : fallback;
		}

		private static string TranslateGoogleCloud(string text, string tl, string apiKey)
		{
			//IL_008c: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a1: Expected O, but got Unknown
			if (!ReadSettingsBool("enableGoogleCloud", fallback: true) || string.IsNullOrWhiteSpace(apiKey) || !ProviderUsable("Google Cloud Translation") || !FirstTry("Google Cloud Translation", text, tl))
			{
				return null;
			}
			string text2 = "q=" + Uri.EscapeDataString(text) + "&target=" + Uri.EscapeDataString(tl) + "&format=text";
			string json = LiveHttp("Google Cloud Translation", HttpMethod.Post, "https://translation.googleapis.com/language/translate/v2", (HttpContent)new StringContent(text2, Encoding.UTF8, "application/x-www-form-urlencoded"), "X-Goog-Api-Key", apiKey.Trim());
			return ProviderJsonHelper.ExtractFirstString(json, "translatedText");
		}

		private static string TranslateAzure(string text, string tl)
		{
			string text2 = ReadSettingsString("azureApiKey");
			if (!ReadSettingsBool("enableAzure", fallback: true) || string.IsNullOrWhiteSpace(text2) || string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			if (UrtOptEngine.IsCoolingDown("Azure Translator"))
			{
				UrtLiveDiag.Outcome("azureCooldown");
				if (Interlocked.Exchange(ref azureCooldownLog, 1) == 0)
				{
					MLog.Info("[URT] Live Azure skipped during global cooldown (" + UrtOptEngine.CooldownRemainingSeconds("Azure Translator") + "s).");
				}
				return null;
			}
			Interlocked.Exchange(ref azureCooldownLog, 0);
			if (!FirstTry("Azure", text, tl))
			{
				return null;
			}
			string text3 = ReadSettingsString("azureRegion");
			TranslationProviderSettings settings = new TranslationProviderSettings
			{
				EnableAzure = true,
				AzureApiKey = text2.Trim(),
				AzureRegion = (string.IsNullOrWhiteSpace(text3) ? "global" : text3.Trim()),
				AzureEndpoint = (ReadSettingsString("azureEndpoint") ?? "")
			};
			string target = (string.IsNullOrEmpty(tl) ? LanguageCodeMapper.ToProviderCode(UniversalRimTranslatorSettings.PlayerTargetLanguage()) : tl);
			Stopwatch stopwatch = Stopwatch.StartNew();
			string text4 = AzureLiveBatcher.Translate(text, target, settings);
			UrtLiveDiag.Http("Azure", text4 != null, stopwatch.ElapsedMilliseconds, 0, 30000);
			if (text4 == null && azureFailLog < 3 && UrtOptEngine.IsCoolingDown("Azure Translator"))
			{
				azureFailLog++;
				MLog.Warn("[URT] Live Azure throttled; global cooldown " + UrtOptEngine.CooldownRemainingSeconds("Azure Translator") + "s.");
			}
			return text4;
		}

		private static string TranslateYandex(string text, string tl)
		{
			//IL_00db: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e2: Expected O, but got Unknown
			//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00bc: Expected O, but got Unknown
			if (!ReadSettingsBool("enableYandex", fallback: true) || !ProviderUsable("Yandex") || !FirstTry("Yandex", text, tl))
			{
				return null;
			}
			string text2 = ReadSettingsString("yandexEndpoint");
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = "https://browser.translate.yandex.net/api/v1/tr.json/translate";
			}
			string text3 = LanguageCodeMapper.ToBaseCode(tl);
			if (string.IsNullOrEmpty(text3))
			{
				text3 = tl;
			}
			string text4 = text2 + "?lang=auto-" + Uri.EscapeDataString(text3) + "&srv=browser_video_translation";
			string text5 = Uri.EscapeDataString(text);
			HttpContent content;
			if (text5.Length > 1800)
			{
				content = (HttpContent)new FormUrlEncodedContent((IEnumerable<KeyValuePair<string, string>>)new KeyValuePair<string, string>[1]
				{
					new KeyValuePair<string, string>("text", text)
				});
			}
			else
			{
				text4 = text4 + "&text=" + text5;
				content = (HttpContent)new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");
			}
			string json = LiveHttp("Yandex", HttpMethod.Post, text4, content);
			List<string> list = ProviderJsonHelper.ExtractStringValues(json, "text");
			return (list.Count == 0) ? null : WebUtility.HtmlDecode(list[0]);
		}

		private static string TranslateGoogleAuto(string text, string tl)
		{
			if (!ReadSettingsBool("enableGoogleWeb", fallback: false) || !ProviderUsable("Google web") || !FirstTry("Google web", text, tl))
			{
				return null;
			}
			string text2 = Uri.EscapeDataString(text);
			if (text2.Length > 7000)
			{
				return null;
			}
			string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(tl) + "&dt=t&q=" + text2;
			string text3 = LiveHttp("Google web", HttpMethod.Get, url, null);
			if (string.IsNullOrEmpty(text3) || text3[0] != '[')
			{
				return null;
			}
			MatchCollection matchCollection = Regex.Matches(text3, "\\[\"((?:\\\\.|[^\"\\\\])*)\",\"((?:\\\\.|[^\"\\\\])*)\"");
			if (matchCollection.Count == 0)
			{
				return null;
			}
			StringBuilder stringBuilder = new StringBuilder();
			for (int i = 0; i < matchCollection.Count; i++)
			{
				stringBuilder.Append(Regex.Unescape(matchCollection[i].Groups[1].Value));
			}
			return stringBuilder.ToString();
		}
	}
	internal static class AzureLiveBatcher
	{
		private sealed class Request
		{
			public string Text;

			public string Target;

			public TranslationProviderSettings Settings;

			public readonly ManualResetEventSlim Done = new ManualResetEventSlim(initialState: false);

			public string Result;
		}

		private static readonly object Gate = new object();

		private static readonly List<Request> Waiting = new List<Request>();

		private static bool flushing;

		private const int CollectMs = 150;

		private const int MaxItems = 25;

		private const int MaxChars = 20000;

		private const int WaitTimeoutMs = 30000;

		public static string Translate(string text, string target, TranslationProviderSettings settings)
		{
			Request request = new Request
			{
				Text = text,
				Target = target,
				Settings = settings
			};
			bool flag = false;
			lock (Gate)
			{
				Waiting.Add(request);
				if (!flushing)
				{
					flushing = true;
					flag = true;
				}
			}
			if (flag)
			{
				ThreadPool.QueueUserWorkItem(delegate
				{
					FlushLoop();
				});
			}
			if (!request.Done.Wait(30000))
			{
				return null;
			}
			return request.Result;
		}

		private static void FlushLoop()
		{
			try
			{
				Thread.Sleep(150);
				while (true)
				{
					List<Request> list;
					lock (Gate)
					{
						if (Waiting.Count == 0)
						{
							flushing = false;
							break;
						}
						string target = Waiting[0].Target;
						list = new List<Request>();
						int num = 0;
						for (int i = 0; i < Waiting.Count; i++)
						{
							if (list.Count >= 25)
							{
								break;
							}
							Request request = Waiting[i];
							if (string.Equals(request.Target, target, StringComparison.OrdinalIgnoreCase))
							{
								int length = (request.Text ?? "").Length;
								if (list.Count > 0 && num + length > 20000)
								{
									break;
								}
								list.Add(request);
								num += length;
							}
						}
						for (int j = 0; j < list.Count; j++)
						{
							Waiting.Remove(list[j]);
						}
					}
					Send(list);
				}
			}
			catch
			{
				lock (Gate)
				{
					for (int k = 0; k < Waiting.Count; k++)
					{
						Waiting[k].Done.Set();
					}
					Waiting.Clear();
					flushing = false;
				}
			}
		}

		private static void Send(List<Request> batch)
		{
			try
			{
				if (UrtOptEngine.IsCoolingDown("Azure Translator"))
				{
					return;
				}
				List<string> texts = new List<string>(batch.Count);
				for (int i = 0; i < batch.Count; i++)
				{
					texts.Add(batch[i].Text ?? "");
				}
				TranslationProviderResult result = UrtOptEngine.GateAsync("Azure Translator", async delegate
				{
					using CancellationTokenSource cts = new CancellationTokenSource(20000);
					return await new AzureTranslateProvider().TranslateBatchAsync(texts, "Auto", batch[0].Target, batch[0].Settings, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
				}).GetAwaiter().GetResult();
				if (result != null && result.Translations != null)
				{
					for (int j = 0; j < batch.Count && j < result.Translations.Count; j++)
					{
						string text = result.Translations[j];
						batch[j].Result = (string.IsNullOrWhiteSpace(text) ? null : text);
					}
				}
			}
			catch
			{
			}
			finally
			{
				for (int k = 0; k < batch.Count; k++)
				{
					batch[k].Done.Set();
				}
			}
		}
	}
	public static class RuntimeTextHighlighter
	{
		private static readonly object Gate = new object();

		private static string sourceText = "";

		private static string translationText = "";

		private static float until;

		private static bool initialized;

		public static void Initialize()
		{
			if (!initialized)
			{
				initialized = true;
				MLog.Info("[Universal RimTranslator] Find in Game highlighter on-demand only (no extra Widgets patches).");
			}
		}

		public static void Arm(string source, string translation, float seconds)
		{
			Initialize();
			lock (Gate)
			{
				sourceText = Normalize(source);
				translationText = Normalize(translation);
				until = Time.realtimeSinceStartup + Mathf.Max(1f, seconds);
			}
			CloseFloatMenus();
		}

		public static void TextDrawPostfix(Rect __0, string __1)
		{
			string text = Normalize(__1);
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			bool flag;
			lock (Gate)
			{
				if (Time.realtimeSinceStartup > until)
				{
					return;
				}
				flag = Matches(text);
			}
			if (flag)
			{
				Rect rect = __0;
				rect.xMin -= 3f;
				rect.yMin -= 3f;
				rect.xMax += 3f;
				rect.yMax += 3f;
				Widgets.DrawBox(rect, 3);
			}
		}

		private static bool IsSupportedTextMethod(MethodInfo method)
		{
			if (method == null || method.IsGenericMethodDefinition || !method.IsStatic)
			{
				return false;
			}
			string name = method.Name;
			if (name != "Label" && name != "ButtonText" && name != "ButtonTextSubtle" && name != "ButtonTextLabeled" && name != "ButtonTextLabeledPct")
			{
				return false;
			}
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length >= 2 && parameters[0].ParameterType == typeof(Rect) && parameters[1].ParameterType == typeof(string);
		}

		private static bool Matches(string text)
		{
			return (!string.IsNullOrEmpty(sourceText) && string.Equals(text, sourceText, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrEmpty(translationText) && string.Equals(text, translationText, StringComparison.OrdinalIgnoreCase));
		}

		private static string Normalize(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}
			string value = text.Trim();
			value = StripRimWorldTags(value);
			return value.Trim();
		}

		private static string StripRimWorldTags(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}
			List<char> list = new List<char>(value.Length);
			bool flag = false;
			foreach (char c in value)
			{
				if (c == '<')
				{
					flag = true;
				}
				else if (c == '>' && flag)
				{
					flag = false;
				}
				else if (!flag)
				{
					list.Add(c);
				}
			}
			return new string(list.ToArray());
		}

		private static void CloseFloatMenus()
		{
			try
			{
				if (Find.WindowStack == null)
				{
					return;
				}
				Type typeFromHandle = typeof(FloatMenu);
				MethodInfo method = Find.WindowStack.GetType().GetMethod("TryRemoveAssignableFromType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(Type) }, null);
				if (method != null)
				{
					method.Invoke(Find.WindowStack, new object[1] { typeFromHandle });
					return;
				}
				MethodInfo[] methods = Find.WindowStack.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				MethodInfo[] array = methods;
				foreach (MethodInfo methodInfo in array)
				{
					if (!string.Equals(methodInfo.Name, "TryRemove", StringComparison.Ordinal))
					{
						continue;
					}
					ParameterInfo[] parameters = methodInfo.GetParameters();
					if (parameters.Length == 0 || parameters[0].ParameterType != typeof(Type))
					{
						continue;
					}
					object[] array2 = new object[parameters.Length];
					array2[0] = typeFromHandle;
					for (int j = 1; j < array2.Length; j++)
					{
						if (parameters[j].ParameterType == typeof(bool))
						{
							array2[j] = true;
						}
						else if (parameters[j].HasDefaultValue)
						{
							array2[j] = parameters[j].DefaultValue;
						}
						else
						{
							array2[j] = null;
						}
					}
					methodInfo.Invoke(Find.WindowStack, array2);
					break;
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Universal RimTranslator] Could not close FloatMenu for Find in Game: " + ex.Message);
			}
		}

		private static void InvokeHarmonyPatch(Type harmonyType, object harmony, MethodBase original, object prefix, object postfix)
		{
			Type type = harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod");
			MethodInfo methodInfo = null;
			MethodInfo[] methods = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
			foreach (MethodInfo methodInfo2 in methods)
			{
				if (string.Equals(methodInfo2.Name, "Patch", StringComparison.Ordinal))
				{
					ParameterInfo[] parameters = methodInfo2.GetParameters();
					if (parameters.Length >= 3 && typeof(MethodBase).IsAssignableFrom(parameters[0].ParameterType) && !(parameters[1].ParameterType != type) && !(parameters[2].ParameterType != type))
					{
						methodInfo = methodInfo2;
						break;
					}
				}
			}
			if (methodInfo == null)
			{
				throw new MissingMethodException("Harmony.Patch overload not found.");
			}
			ParameterInfo[] parameters2 = methodInfo.GetParameters();
			object[] array = new object[parameters2.Length];
			array[0] = original;
			if (parameters2.Length > 1)
			{
				array[1] = prefix;
			}
			if (parameters2.Length > 2)
			{
				array[2] = postfix;
			}
			for (int j = 3; j < array.Length; j++)
			{
				array[j] = null;
			}
			methodInfo.Invoke(harmony, array);
		}
	}
	public static class RuntimeTextTranslator
	{
		private struct HitVerdict
		{
			public string Hit;

			public bool Ok;
		}

		private sealed class RecipeSource
		{
			public string Label;

			public string JobString;

			public string Description;

			public string ProductLabel;

			public string ItemText;

			public int Count;

			public RecipeDef Recipe;
		}

		private sealed class GenSource
		{
			public Def Target;

			public string Field;

			public string Text;

			public string BaseLabel;
		}

		private static readonly object Gate = new object();

		private static Dictionary<string, string> translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private static volatile ConcurrentDictionary<string, string> hotExact = new ConcurrentDictionary<string, string>(UrtDrawKeyComparer.Instance);

		private static readonly string MarkTarget = new string('\u0001', 1);

		private static readonly string MarkPending = new string('\u0002', 1);

		private static Dictionary<string, string> textTargetTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private static HashSet<string> ambiguousTextTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, string> polishedCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		private static readonly ConcurrentDictionary<string, HitVerdict> hitVerdicts = new ConcurrentDictionary<string, HitVerdict>(StringComparer.Ordinal);

		private static bool loggedB76;

		private static readonly ConcurrentQueue<string> deferredVisible = new ConcurrentQueue<string>();

		private static readonly ConcurrentDictionary<string, byte> deferredSeen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

		private static int deferredReadyAt;

		private static bool loggedB78;

		private static List<KeyValuePair<string, string>> longPhrases = new List<KeyValuePair<string, string>>();

		private static readonly ConcurrentDictionary<string, string> sourceLanguageCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		private const int SourceLanguageCacheLimit = 20000;

		private static bool initialized;

		private static bool patchFailed;

		private static int patchFailLog;

		private static object lastResearchWindow;

		private static int researchCaptureCursor;

		private static int researchCaptureTick;

		private static int researchCaptureLogTick;

		[ThreadStatic]
		private static int letterRenderDepth;

		private static FieldInfo[] researchHoverFields;

		private static Type researchHoverType;

		private static readonly long DrawBudgetTicks = Math.Max(1L, Stopwatch.Frequency / 1000);

		private const int MaxLiveHandoffPerFrame = 2;

		private static long drawSpent;

		private static bool drawOver;

		private static int handoffCount;

		private static string handoff0;

		private static string handoff1;

		private static bool handoff0Letter;

		private static bool handoff1Letter;

		private static int windowRenderDepth;

		private static int tooltipRenderDepth;

		private const int ParkMs = 45000;

		private const int MaxParked = 4000;

		private static readonly Queue<KeyValuePair<string, int>> parked = new Queue<KeyValuePair<string, int>>();

		private static long statFrames;

		private static long statCalls;

		private static long statHits;

		private static long statDrawTicks;

		private static long statDrawMax;

		private static long statOverFrames;

		private static long statPumpTicks;

		private static long statNudges;

		private static long statRegCount;

		private static long statRegTicks;

		private static long statMemo;

		private static int statNextLog;

		private static int statLines;

		private const int StatLinesMax = 10;

		private const int MemoSlots = 16384;

		private const int MemoMask = 16383;

		private static readonly string[] memoKey = new string[16384];

		private static readonly string[] memoVal = new string[16384];

		private static readonly int[] memoGen = new int[16384];

		private static readonly bool[] memoMiss = new bool[16384];

		private static int gen = 1;

		private static int hitValidFrom = 1;

		private static int missValidFrom = 1;

		private static volatile bool liveDirty;

		private static int lastMissRefresh;

		public static int OwnWindowDepth;

		private static bool pickArmed;

		private static bool pickKeep;

		private static string pickDrawn;

		private static string pickShown;

		private static float pickArea;

		private static KeyBindingDef keyRetranslate;

		private static KeyBindingDef keyKeep;

		private static bool keysLooked;

		private static List<GenSource> genSources;

		private static List<RecipeSource> recipeSources;

		private static bool targetIsCzech = true;

		private static bool loggedRecipeLanguage;

		private static bool lowerFirstLabels;

		private static List<string> activeLanguageTexts;

		private static readonly Dictionary<string, string> PhraseFallbacks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "could not find any more forced work. The remaining targets are probably reserved or unreachable.", "nenašla další nucenou práci. Zbývající cíle jsou rezervované nebo nedostupné." },
			{ "Start a conversation with", "Začít konverzaci s" },
			{ "Consider recruiting", "Zvážit verbování" },
			{ "Cannot seduce", "Nelze svádět" },
			{ "Cannot converse with", "Nelze mluvit s" },
			{ "Rape", "Znásilnění" },
			{ "(Reverse)", "(Zpětný chod)" },
			{ "Have sex with", "Mít sex s" },
			{ "View all bills.", "Zobrazit všechny zakázky." },
			{ "Mint bill menu", "Menu zakázek Mint" },
			{ "Mint gear menu", "Menu výbavy Mint" },
			{ "Socialize with", "Socializovat s" },
			{ "Low opinion of me", "má o mně špatné mínění" },
			{ "had some lovin' too recently", "měl/a nedávno intimní styk" },
			{ "is busy with an important job", "má důležitou práci" },
			{ "Open altar desired", "Je požadován otevřený oltář" },
			{ "Free rector", "Volný rektor" },
			{ "Resistance army personnel", "příslušník odbojové armády" },
			{ "Factory wired terminal", "Tovární drátový terminál" },
			{ "Even outside of battle, a perfect space for rest is also necessary.", "I mimo bitvu je potřeba dokonalé místo k odpočinku." }
		};

		private static readonly Regex UrtMask = new Regex("[_ ]*URT[_ ]*(?:PLACEHOLDER|z[áa]stupn[ýy]\\s*symbol|placeholder|token)?[_ ]*(\\d+)[_ ]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public static bool IsInitialized => initialized;

		public static void Initialize()
		{
			if (initialized || patchFailed)
			{
				return;
			}
			initialized = true;
			try
			{
				Type type = Type.GetType("HarmonyLib.Harmony, 0Harmony");
				if (type == null)
				{
					MLog.Warn("[Universal RimTranslator] Harmony runtime not found; runtime translations disabled.");
					patchFailed = true;
					return;
				}
				object obj = Activator.CreateInstance(type, "UniversalRimTranslator.RuntimeTranslation");
				Type type2 = type.Assembly.GetType("HarmonyLib.HarmonyMethod");
				if (type2 == null)
				{
					patchFailed = true;
					return;
				}
				MethodInfo method = typeof(RuntimeTextTranslator).GetMethod("TextPrefix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method2 = typeof(RuntimeTextTranslator).GetMethod("StringPrefix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method3 = typeof(RuntimeTextTranslator).GetMethod("TaggedLabelPrefix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method4 = typeof(RuntimeTextTranslator).GetMethod("ResultPostfix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method5 = typeof(RuntimeTextTranslator).GetMethod("ResultPostfixTagged", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method6 = typeof(RuntimeTextTranslator).GetMethod("NotificationResultPostfix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method7 = typeof(RuntimeTextTranslator).GetMethod("NotificationResultPostfixTagged", BindingFlags.Static | BindingFlags.Public);
				if (method == null)
				{
					throw new MissingMethodException("TextPrefix was not found.");
				}
				int num = 0;
				List<MethodInfo> list = new List<MethodInfo>();
				Collect(typeof(Widgets), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, list);
				Collect(typeof(TooltipHandler), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, list);
				Collect(typeof(Listing_Standard), BindingFlags.Instance | BindingFlags.Public, list);
				Collect(typeof(Messages), BindingFlags.Static | BindingFlags.Public, list);
				Collect(typeof(Command), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, list);
				Collect(typeof(Text), BindingFlags.Static | BindingFlags.Public, list);
				Type type3 = Type.GetType("RimWorld.Command_Ability, Assembly-CSharp");
				if (type3 != null)
				{
					Collect(type3, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, list);
				}
				Type type4 = Type.GetType("RimWorld.Ability, Assembly-CSharp");
				if (type4 != null)
				{
					Collect(type4, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, list);
				}
				Type type5 = Type.GetType("RimWorld.Alert, Assembly-CSharp");
				if (type5 != null)
				{
					Collect(type5, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, list);
				}
				Type type6 = Type.GetType("Verse.Letter, Assembly-CSharp");
				if (type6 != null)
				{
					Collect(type6, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, list);
				}
				string[] array = new string[4] { "RimWorld.Command_Action, Assembly-CSharp", "RimWorld.Command_Toggle, Assembly-CSharp", "RimWorld.Command_VerbTarget, Assembly-CSharp", "Verse.Designator, Assembly-CSharp" };
				foreach (string typeName in array)
				{
					Type type7 = Type.GetType(typeName);
					if (type7 != null)
					{
						Collect(type7, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, list);
					}
				}
				string[] array2 = new string[62]
				{
					"RimWorld.Quest, Assembly-CSharp", "Verse.ChoiceLetter, Assembly-CSharp", "Verse.StandardLetter, Assembly-CSharp", "RimWorld.Inspiration, Assembly-CSharp", "RimWorld.InspirationDef, Assembly-CSharp", "RimWorld.ThoughtDef, Assembly-CSharp", "RimWorld.WorkGiver, Assembly-CSharp", "RimWorld.Bill, Assembly-CSharp", "RimWorld.Bill_Production, Assembly-CSharp", "RimWorld.ResearchProjectDef, Assembly-CSharp",
					"Verse.TipSignal, Assembly-CSharp", "RimWorld.IncidentDef, Assembly-CSharp", "RimWorld.GameCondition, Assembly-CSharp", "RimWorld.GameConditionDef, Assembly-CSharp", "RimWorld.SkillDef, Assembly-CSharp", "RimWorld.Need, Assembly-CSharp", "RimWorld.StatDef, Assembly-CSharp", "RimWorld.WorkTypeDef, Assembly-CSharp", "RimWorld.TraitDef, Assembly-CSharp", "RimWorld.Trait, Assembly-CSharp",
					"RimWorld.Faction, Assembly-CSharp", "RimWorld.FactionDef, Assembly-CSharp", "RimWorld.PawnKindDef, Assembly-CSharp", "RimWorld.AbilityDef, Assembly-CSharp", "RimWorld.Precept, Assembly-CSharp", "RimWorld.ITab, Assembly-CSharp", "RimWorld.HealthCardUtility, Assembly-CSharp", "RimWorld.CharacterCardUtility, Assembly-CSharp", "Verse.FloatMenuOption, Assembly-CSharp", "Verse.FloatMenu, Assembly-CSharp",
					"Verse.Window, Assembly-CSharp", "Verse.LogEntry, Assembly-CSharp", "RimWorld.PlayLogEntry_Interaction, Assembly-CSharp", "RimWorld.Dialog_NodeTree, Assembly-CSharp", "RimWorld.Dialog_MessageBox, Assembly-CSharp", "RimWorld.Dialog_InfoCard, Assembly-CSharp", "RimWorld.Dialog_Trade, Assembly-CSharp", "RimWorld.Dialog_BeginRitual, Assembly-CSharp", "RimWorld.DiaNode, Assembly-CSharp", "RimWorld.Tradeable, Assembly-CSharp",
					"RimWorld.Book, Assembly-CSharp", "RimWorld.MainButtonDef, Assembly-CSharp", "RimWorld.MainButtonWorker, Assembly-CSharp", "RimWorld.MainTabWindow, Assembly-CSharp", "RimWorld.Page, Assembly-CSharp", "Verse.Message, Assembly-CSharp", "RimWorld.QuestPart, Assembly-CSharp", "RimWorld.QuestPart_Choice, Assembly-CSharp", "RimWorld.Alert_Custom, Assembly-CSharp", "RimWorld.MainTabWindow_Quests, Assembly-CSharp",
					"RimWorld.MainTabWindow_Research, Assembly-CSharp", "RimWorld.Dialog_BillConfig, Assembly-CSharp", "RimWorld.ITab_Bills, Assembly-CSharp", "RimWorld.ITab_Pawn_Gear, Assembly-CSharp", "RimWorld.ITab_Pawn_Needs, Assembly-CSharp", "RimWorld.ITab_Pawn_Character, Assembly-CSharp", "RimWorld.InspectPaneFiller, Assembly-CSharp", "Verse.InspectPaneUtility, Assembly-CSharp", "RimWorld.TrainingCardUtility, Assembly-CSharp", "RimWorld.SkillUI, Assembly-CSharp",
					"RimWorld.WidgetsWork, Assembly-CSharp", "Verse.Listing_Tree, Assembly-CSharp"
				};
				for (int j = 0; j < array2.Length; j++)
				{
					Type type8 = Type.GetType(array2[j]);
					if (type8 != null)
					{
						Collect(type8, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, list);
					}
				}
				HashSet<MethodInfo> hashSet = new HashSet<MethodInfo>();
				foreach (MethodInfo item in list)
				{
					MethodInfo methodInfo = item;
					if (methodInfo != null && methodInfo.DeclaringType != null && methodInfo.ReflectedType != methodInfo.DeclaringType)
					{
						ParameterInfo[] parameters = methodInfo.GetParameters();
						Type[] array3 = new Type[parameters.Length];
						for (int k = 0; k < parameters.Length; k++)
						{
							array3[k] = parameters[k].ParameterType;
						}
						MethodInfo method8 = methodInfo.DeclaringType.GetMethod(methodInfo.Name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, array3, null);
						if (method8 == null)
						{
							continue;
						}
						methodInfo = method8;
					}
					if (!IsSupported(methodInfo) || !hashSet.Add(methodInfo))
					{
						continue;
					}
					MethodInfo methodInfo2 = method;
					MethodInfo methodInfo3 = null;
					MethodInfo methodInfo4 = null;
					try
					{
						ParameterInfo[] parameters2 = methodInfo.GetParameters();
						if (IsStringGetter(methodInfo.Name) || IsStringInspect(methodInfo.Name) || methodInfo.Name == "Translate" || methodInfo.Name == "TranslateKeyed")
						{
							methodInfo2 = null;
							methodInfo3 = ((!IsLetterType(methodInfo.DeclaringType)) ? ((methodInfo.ReturnType.FullName == "Verse.TaggedString") ? method5 : method4) : ((methodInfo.ReturnType.FullName == "Verse.TaggedString") ? method7 : method6));
							if (methodInfo3 == null)
							{
								methodInfo3 = method4;
							}
						}
						else if (parameters2.Length >= 2 && parameters2[0].ParameterType == typeof(Rect) && parameters2[1].ParameterType.FullName == "Verse.TaggedString")
						{
							methodInfo2 = method3;
						}
						else if (parameters2.Length >= 1 && parameters2[0].ParameterType == typeof(string))
						{
							methodInfo2 = method2;
						}
						object obj2 = ((methodInfo2 == null) ? null : Activator.CreateInstance(type2, methodInfo2));
						object obj3 = ((methodInfo3 == null) ? null : Activator.CreateInstance(type2, methodInfo3));
						methodInfo4 = type.GetMethod("Patch", new Type[5]
						{
							typeof(MethodBase),
							type2,
							type2,
							type2,
							type2
						});
						if (methodInfo4 != null)
						{
							methodInfo4.Invoke(obj, new object[5] { methodInfo, obj2, obj3, null, null });
							num++;
						}
					}
					catch (Exception ex)
					{
						bool flag = false;
						if (methodInfo3 != null && methodInfo4 != null)
						{
							try
							{
								MethodInfo methodInfo5 = ((methodInfo3 == method5) ? method4 : method5);
								object obj4 = Activator.CreateInstance(type2, methodInfo5);
								methodInfo4.Invoke(obj, new object[5] { methodInfo, null, obj4, null, null });
								num++;
								flag = true;
							}
							catch
							{
							}
						}
						if (!flag && patchFailLog < 6)
						{
							patchFailLog++;
							Exception ex2 = ex.InnerException ?? ex;
							string text = ((methodInfo.DeclaringType == null) ? "" : (methodInfo.DeclaringType.Name + "."));
							MLog.Info("[Universal RimTranslator] Runtime translation patch skipped " + text + methodInfo.Name + ": " + ex2.GetType().Name + " " + ex2.Message);
						}
					}
				}
				if (PatchLetterRenderScope(obj, type, type2))
				{
					num++;
				}
				if (PatchWindowRenderScope(obj, type, type2))
				{
					num++;
				}
				if (PatchTooltipRenderScope(obj, type, type2))
				{
					num++;
				}
				if (PatchResearchWindowCapture(obj, type, type2))
				{
					num++;
				}
				MLog.Info("[URT] Runtime patches applied: " + num);
				MLog.Info("[Universal RimTranslator] Runtime translation enabled; patched " + num + " text methods (labels, buttons, tooltips, inspect).");
				MLog.Info("[URT] b112: glossary no longer changes {PAWN}/{RESEARCH}; 50 % no longer a broken token; saved translations count as done (pending goes down).");
			}
			catch (Exception ex3)
			{
				patchFailed = true;
				MLog.Warn("[Universal RimTranslator] Runtime translation patch failed: " + ex3.GetType().Name + ": " + ex3.Message);
			}
		}

		private static string RuntimeKey(string source, string sourceLanguage = null, string targetLanguage = null)
		{
			string text = LanguageCodeMapper.Canonical(targetLanguage);
			if (string.IsNullOrWhiteSpace(text))
			{
				text = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			}
			string text2 = sourceLanguage;
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = GetSourceLanguageCached(source ?? "");
			}
			string text3 = LanguageCodeMapper.Canonical(text2);
			if (string.IsNullOrWhiteSpace(text3) || string.Equals(text3, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(text3, "Unknown", StringComparison.OrdinalIgnoreCase))
			{
				text3 = "English";
			}
			return UrtOptEngine.TextKey(source ?? "", text3, text);
		}

		private static string TextTargetKey(string source, string targetLanguage)
		{
			return Normalize(source) + "\n" + LanguageCodeMapper.Canonical(targetLanguage);
		}

		private static void AddTextTargetFallback(string source, string targetLanguage, string translation)
		{
			AddTextTargetFallback(textTargetTranslations, ambiguousTextTarget, source, targetLanguage, translation);
		}

		private static void AddTextTargetFallback(Dictionary<string, string> textTarget, HashSet<string> ambiguous, string source, string targetLanguage, string translation)
		{
			if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation))
			{
				return;
			}
			string text = TextTargetKey(source, targetLanguage);
			if (ambiguous.Contains(text))
			{
				return;
			}
			if (textTarget.TryGetValue(text, out var value))
			{
				if (!string.Equals(value, translation, StringComparison.Ordinal))
				{
					textTarget.Remove(text);
					ambiguous.Add(text);
				}
			}
			else
			{
				textTarget[text] = translation;
			}
		}

		private static string GetSourceLanguageCached(string source)
		{
			string text = source ?? "";
			if (sourceLanguageCache.TryGetValue(text, out var value))
			{
				return value ?? "";
			}
			value = LanguageDetector.DetectEmbeddedSourceLanguage(text) ?? "";
			if (sourceLanguageCache.Count >= 20000)
			{
				sourceLanguageCache.Clear();
			}
			sourceLanguageCache.TryAdd(text, value);
			return value;
		}

		public static void Refresh(IEnumerable<CachedTranslation> entries)
		{
			if (!initialized && !patchFailed && !UrtMainThread.IsMain)
			{
				// b114: the first Refresh installs the Harmony patches -> only on the main thread
				List<CachedTranslation> copy = (entries == null) ? new List<CachedTranslation>() : entries.ToList();
				UrtMainThread.Post(delegate
				{
					Refresh(copy);
				});
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			bool flag = false;
			try
			{
				flag = UnityData.IsInMainThread;
			}
			catch
			{
			}
			try
			{
				RefreshCore(entries);
			}
			finally
			{
				UrtHitch.Note("Refresh" + (flag ? "(main)" : "(worker)"), Stopwatch.GetTimestamp() - timestamp);
			}
		}

		private static void RefreshCore(IEnumerable<CachedTranslation> entries)
		{
			Initialize();
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> textTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
			ConcurrentDictionary<string, string> concurrentDictionary = new ConcurrentDictionary<string, string>(UrtDrawKeyComparer.Instance);
			List<string> list2 = new List<string>();
			targetIsCzech = string.Equals(LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage()), "Czech", StringComparison.OrdinalIgnoreCase);
			if (entries != null)
			{
				foreach (CachedTranslation entry in entries)
				{
					if (entry == null || string.IsNullOrWhiteSpace(entry.SourceText) || string.IsNullOrWhiteSpace(entry.Translation))
					{
						continue;
					}
					string text = Normalize(entry.SourceText);
					string text2 = entry.Translation.Trim();
					if (!string.IsNullOrEmpty(text) && !string.Equals(text, Normalize(text2), StringComparison.OrdinalIgnoreCase) && TranslationCache.IsUsableTranslation(entry, entry.SourceText))
					{
						string key = RuntimeKey(entry.SourceText, entry.SourceLanguage, entry.TargetLanguage);
						dictionary[key] = text2;
						AddTextTargetFallback(textTarget, ambiguous, entry.SourceText, entry.TargetLanguage, text2);
						if (KeepForDraw(text, text2))
						{
							concurrentDictionary.TryAdd(text, text2);
							list2.Add(text2);
						}
						if (text.Length >= 22 && text.Length <= 160 && text.IndexOf(' ') >= 0 && list.Count < 80)
						{
							list.Add(new KeyValuePair<string, string>(text, text2));
						}
					}
				}
			}
			int num = 0;
			try
			{
				num = AddGeneratedRecipes(concurrentDictionary, list2);
			}
			catch
			{
			}
			try
			{
				if (GameLanguageIsTarget())
				{
					AddGeneratedDefs(concurrentDictionary, list2);
				}
			}
			catch
			{
			}
			try
			{
				AddActiveLanguageTexts(list2);
			}
			catch
			{
			}
			try
			{
				foreach (string item in UrtFix.KeepList())
				{
					concurrentDictionary[Normalize(item)] = MarkTarget;
				}
			}
			catch
			{
			}
			for (int i = 0; i < list2.Count; i++)
			{
				concurrentDictionary.TryAdd(list2[i], MarkTarget);
			}
			lock (Gate)
			{
				translations = dictionary;
				textTargetTranslations = textTarget;
				ambiguousTextTarget = ambiguous;
				longPhrases = list;
				hotExact = concurrentDictionary;
			}
			InvalidateAllMemo();
			sourceLanguageCache.Clear();
			polishedCache.Clear();
			hitVerdicts.Clear();
		}

		private static string PolishHit(string hit)
		{
			if (string.IsNullOrEmpty(hit))
			{
				return hit;
			}
			if (polishedCache.TryGetValue(hit, out var value))
			{
				return value;
			}
			value = UrtGlossary.After(hit);
			if (polishedCache.Count > 20000)
			{
				polishedCache.Clear();
			}
			polishedCache.TryAdd(hit, value);
			return value;
		}

		public static void TextPrefix(Rect __0, ref string __1)
		{
			try
			{
				if (pickArmed)
				{
					string drawn = __1;
					Draw(ref __1);
					Pick(__0, drawn, __1);
				}
				else
				{
					Draw(ref __1);
				}
			}
			catch
			{
			}
		}

		private static bool DisplayContext()
		{
			return windowRenderDepth > 0 || tooltipRenderDepth > 0 || letterRenderDepth > 0 || IsGuiEventActive();
		}

		public static void StringPrefix(ref string __0)
		{
			try
			{
				Draw(ref __0);
			}
			catch
			{
			}
		}

		public static void TaggedLabelPrefix(Rect __0, ref TaggedString __1)
		{
			try
			{
				string value = __1.RawText;
				string text = value;
				Draw(ref value);
				if (pickArmed)
				{
					Pick(__0, text, value);
				}
				if (!string.IsNullOrEmpty(value) && (object)value != text)
				{
					__1 = new TaggedString(value);
				}
			}
			catch
			{
			}
		}

		public static void ResultPostfix(ref string __result)
		{
			UrtFrameWatch.HookCalls++;
			if (!DisplayContext() || letterRenderDepth > 0)
			{
				return;
			}
			try
			{
				Draw(ref __result);
			}
			catch
			{
			}
		}

		public static void ResultPostfixTagged(ref TaggedString __result)
		{
			UrtFrameWatch.HookCalls++;
			if (!DisplayContext() || letterRenderDepth > 0)
			{
				return;
			}
			try
			{
				string value = __result.RawText;
				string text = value;
				Draw(ref value);
				if (!string.IsNullOrEmpty(value) && (object)value != text)
				{
					__result = new TaggedString(value);
				}
			}
			catch
			{
			}
		}

		public static void NotificationResultPostfix(ref string __result)
		{
			if (!DisplayContext())
			{
				return;
			}
			try
			{
				Draw(ref __result);
			}
			catch
			{
			}
		}

		public static void NotificationResultPostfixTagged(ref TaggedString __result)
		{
			if (!DisplayContext())
			{
				return;
			}
			try
			{
				string value = __result.RawText;
				string text = value;
				Draw(ref value);
				if (!string.IsNullOrEmpty(value) && (object)value != text)
				{
					__result = new TaggedString(value);
				}
			}
			catch
			{
			}
		}

		private static bool IsGuiEventActive()
		{
			try
			{
				return Event.current != null;
			}
			catch
			{
				return false;
			}
		}

		public static void WindowRenderScopePrefix()
		{
			windowRenderDepth++;
		}

		public static Exception WindowRenderScopeFinalizer(Exception __exception)
		{
			if (windowRenderDepth > 0)
			{
				windowRenderDepth--;
			}
			return __exception;
		}

		public static void TooltipRenderScopePrefix()
		{
			tooltipRenderDepth++;
		}

		public static Exception TooltipRenderScopeFinalizer(Exception __exception)
		{
			if (tooltipRenderDepth > 0)
			{
				tooltipRenderDepth--;
			}
			return __exception;
		}

		public static void LetterRenderScopePrefix()
		{
			letterRenderDepth++;
		}

		public static void ResearchWindowCapturePostfix(object __instance)
		{
			try
			{
				if (Current.ProgramState != ProgramState.Playing || !RuntimeLiveQueue.Active)
				{
					return;
				}
				int tickCount = Environment.TickCount;
				if (lastResearchWindow != __instance || tickCount - researchCaptureTick > 1500)
				{
					lastResearchWindow = __instance;
					researchCaptureCursor = 0;
				}
				researchCaptureTick = tickCount;
				int queued = 0;
				NudgeHoveredResearch(__instance, ref queued);
				List<ResearchProjectDef> allDefsListForReading = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
				if (allDefsListForReading == null || allDefsListForReading.Count == 0)
				{
					return;
				}
				int num = 0;
				while (num < allDefsListForReading.Count && queued < 6)
				{
					if (researchCaptureCursor >= allDefsListForReading.Count)
					{
						researchCaptureCursor = 0;
					}
					ResearchProjectDef project = allDefsListForReading[researchCaptureCursor++];
					num++;
					NudgeResearchProject(project, ref queued);
				}
				if (queued > 0 && tickCount - researchCaptureLogTick > 1500)
				{
					researchCaptureLogTick = tickCount;
					MLog.Info("[URT] b82 the research under the cursor is translated first. queued " + queued + " cursor=" + researchCaptureCursor + "/" + allDefsListForReading.Count);
				}
			}
			catch
			{
			}
		}

		private static bool PatchResearchWindowCapture(object harmony, Type harmonyType, Type harmonyMethodType)
		{
			try
			{
				Type type = Type.GetType("RimWorld.MainTabWindow_Research, Assembly-CSharp");
				if (type == null)
				{
					return false;
				}
				MethodInfo method = type.GetMethod("DoWindowContents", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(Rect) }, null);
				if (method == null)
				{
					return false;
				}
				MethodInfo method2 = typeof(RuntimeTextTranslator).GetMethod("ResearchWindowCapturePostfix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method3 = harmonyType.GetMethod("Patch", new Type[5]
				{
					typeof(MethodBase),
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType
				});
				if (method2 == null || method3 == null)
				{
					return false;
				}
				object obj = Activator.CreateInstance(harmonyMethodType, method2);
				method3.Invoke(harmony, new object[5] { method, null, obj, null, null });
				MLog.Info("[URT] Research window capture patched for targeted Live queueing.");
				return true;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex.InnerException ?? ex;
				if (patchFailLog < 8)
				{
					patchFailLog++;
					MLog.Info("[Universal RimTranslator] Research capture hook skipped: " + ex2.GetType().Name + " " + ex2.Message);
				}
				return false;
			}
		}

		private static void NudgeHoveredResearch(object window, ref int queued)
		{
			if (window == null || queued >= 6)
			{
				return;
			}
			Type type = window.GetType();
			if (researchHoverType != type)
			{
				researchHoverType = type;
				List<FieldInfo> list = new List<FieldInfo>();
				Type type2 = type;
				while (type2 != null && type2 != typeof(object))
				{
					FieldInfo[] fields = type2.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					for (int i = 0; i < fields.Length; i++)
					{
						if (fields[i].FieldType == typeof(ResearchProjectDef))
						{
							list.Add(fields[i]);
						}
					}
					type2 = type2.BaseType;
				}
				researchHoverFields = list.ToArray();
			}
			if (researchHoverFields == null)
			{
				return;
			}
			for (int j = 0; j < researchHoverFields.Length; j++)
			{
				if (queued >= 6)
				{
					break;
				}
				ResearchProjectDef project = researchHoverFields[j].GetValue(window) as ResearchProjectDef;
				NudgeResearchProject(project, ref queued);
			}
		}

		private static void NudgeResearchProject(ResearchProjectDef project, ref int queued)
		{
			if (project == null || queued >= 6)
			{
				return;
			}
			string label = project.label;
			if (!string.IsNullOrWhiteSpace(label) && RuntimeLiveQueue.Nudge(label.Trim()))
			{
				queued++;
			}
			if (queued < 6)
			{
				string description = project.description;
				if (!string.IsNullOrWhiteSpace(description) && RuntimeLiveQueue.Nudge(description.Trim()))
				{
					queued++;
				}
			}
		}

		public static Exception LetterRenderScopeFinalizer(Exception __exception)
		{
			if (letterRenderDepth > 0)
			{
				letterRenderDepth--;
			}
			return __exception;
		}

		private static bool PatchWindowRenderScope(object harmony, Type harmonyType, Type harmonyMethodType)
		{
			try
			{
				Type type = Type.GetType("Verse.Window, Assembly-CSharp");
				if (type == null)
				{
					return false;
				}
				MethodInfo method = type.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
				if (method == null)
				{
					return false;
				}
				MethodInfo method2 = typeof(RuntimeTextTranslator).GetMethod("WindowRenderScopePrefix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method3 = typeof(RuntimeTextTranslator).GetMethod("WindowRenderScopeFinalizer", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method4 = harmonyType.GetMethod("Patch", new Type[5]
				{
					typeof(MethodBase),
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType
				});
				if (method2 == null || method3 == null || method4 == null)
				{
					return false;
				}
				object obj = Activator.CreateInstance(harmonyMethodType, method2);
				object obj2 = Activator.CreateInstance(harmonyMethodType, method3);
				method4.Invoke(harmony, new object[5] { method, obj, null, null, obj2 });
				MLog.Info("[URT] Window render scope patched for Live capture.");
				return true;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex.InnerException ?? ex;
				MLog.Info("[Universal RimTranslator] Window render hook skipped: " + ex2.GetType().Name + " " + ex2.Message);
				return false;
			}
		}

		private static bool PatchTooltipRenderScope(object harmony, Type harmonyType, Type harmonyMethodType)
		{
			try
			{
				Type type = Type.GetType("Verse.TooltipHandler, Assembly-CSharp");
				if (type == null)
				{
					return false;
				}
				MethodInfo method = typeof(RuntimeTextTranslator).GetMethod("TooltipRenderScopePrefix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method2 = typeof(RuntimeTextTranslator).GetMethod("TooltipRenderScopeFinalizer", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method3 = harmonyType.GetMethod("Patch", new Type[5]
				{
					typeof(MethodBase),
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType
				});
				if (method == null || method2 == null || method3 == null)
				{
					return false;
				}
				object obj = Activator.CreateInstance(harmonyMethodType, method);
				object obj2 = Activator.CreateInstance(harmonyMethodType, method2);
				bool flag = false;
				MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (MethodInfo methodInfo in methods)
				{
					if (!(methodInfo == null) && (!(methodInfo.Name != "TipRegion") || !(methodInfo.Name != "TipRegionByKey")))
					{
						try
						{
							method3.Invoke(harmony, new object[5] { methodInfo, obj, null, null, obj2 });
							flag = true;
						}
						catch
						{
						}
					}
				}
				if (flag)
				{
					MLog.Info("[URT] Tooltip render scope patched for Live capture.");
				}
				return flag;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex.InnerException ?? ex;
				MLog.Info("[Universal RimTranslator] Tooltip render hook skipped: " + ex2.GetType().Name + " " + ex2.Message);
				return false;
			}
		}

		private static bool PatchLetterRenderScope(object harmony, Type harmonyType, Type harmonyMethodType)
		{
			try
			{
				Type type = Type.GetType("Verse.LetterStack, Assembly-CSharp");
				if (type == null)
				{
					return false;
				}
				MethodInfo method = type.GetMethod("LettersOnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(float) }, null);
				if (method == null)
				{
					return false;
				}
				MethodInfo method2 = typeof(RuntimeTextTranslator).GetMethod("LetterRenderScopePrefix", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method3 = typeof(RuntimeTextTranslator).GetMethod("LetterRenderScopeFinalizer", BindingFlags.Static | BindingFlags.Public);
				MethodInfo method4 = harmonyType.GetMethod("Patch", new Type[5]
				{
					typeof(MethodBase),
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType,
					harmonyMethodType
				});
				if (method2 == null || method3 == null || method4 == null)
				{
					return false;
				}
				object obj = Activator.CreateInstance(harmonyMethodType, method2);
				object obj2 = Activator.CreateInstance(harmonyMethodType, method3);
				method4.Invoke(harmony, new object[5] { method, obj, null, null, obj2 });
				return true;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex.InnerException ?? ex;
				MLog.Info("[Universal RimTranslator] Notification render hook skipped: " + ex2.GetType().Name + " " + ex2.Message);
				return false;
			}
		}

		private static bool IsLetterType(Type type)
		{
			Type type2 = type;
			while (type2 != null)
			{
				if (string.Equals(type2.FullName, "Verse.Letter", StringComparison.Ordinal))
				{
					return true;
				}
				type2 = type2.BaseType;
			}
			return false;
		}

		public static bool Has(string source)
		{
			if (string.IsNullOrWhiteSpace(source))
			{
				return false;
			}
			string key = RuntimeKey(source);
			lock (Gate)
			{
				return translations.ContainsKey(key);
			}
		}

		private static void Draw(ref string value)
		{
			UrtFrameWatch.HookCalls++;
			if (OwnWindowDepth > 0)
			{
				return;
			}
			string text = value;
			if (text == null || text.Length < 3 || Current.ProgramState != ProgramState.Playing || !UnityData.IsInMainThread)
			{
				return;
			}
			statCalls++;
			int num = RuntimeHelpers.GetHashCode(text) & 0x3FFF;
			if ((object)memoKey[num] == text && memoGen[num] >= (memoMiss[num] ? missValidFrom : hitValidFrom))
			{
				statMemo++;
				string text2 = memoVal[num];
				if (text2 != null)
				{
					value = text2;
					statHits++;
				}
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			string value2;
			bool flag = hotExact.TryGetValue(text, out value2);
			bool flag2 = flag && IsTranslation(value2);
			if (flag2)
			{
				value2 = MatchFirstCase(text, value2);
			}
			if (flag2)
			{
				value = value2;
				statHits++;
			}
			memoKey[num] = text;
			memoVal[num] = (flag2 ? value2 : null);
			memoMiss[num] = !flag || (object)value2 == MarkPending;
			memoGen[num] = gen;
			if (!flag && !drawOver && handoffCount < 2 && RuntimeLiveQueue.Active && (object)text != handoff0 && (object)text != handoff1)
			{
				bool flag3 = letterRenderDepth > 0;
				if (handoffCount == 0)
				{
					handoff0 = text;
					handoff0Letter = flag3;
				}
				else
				{
					handoff1 = text;
					handoff1Letter = flag3;
				}
				handoffCount++;
			}
			drawSpent += Stopwatch.GetTimestamp() - timestamp;
			if (drawSpent >= DrawBudgetTicks)
			{
				drawOver = true;
			}
		}

		private static string MatchFirstCase(string source, string hit)
		{
			if (hit.Length == 0 || source.Length == 0)
			{
				return hit;
			}
			if (char.IsUpper(source[0]) && char.IsLower(hit[0]))
			{
				return char.ToUpperInvariant(hit[0]) + hit.Substring(1);
			}
			return hit;
		}

		private static void Pick(Rect rect, string drawn, string shown)
		{
			if (OwnWindowDepth <= 0 && !string.IsNullOrEmpty(drawn) && Mouse.IsOver(rect))
			{
				float num = rect.width * rect.height;
				if (pickDrawn == null || num <= pickArea)
				{
					pickDrawn = drawn;
					pickShown = shown;
					pickArea = num;
				}
			}
		}

		private static void PollFixKeys()
		{
			if (pickArmed)
			{
				pickArmed = false;
				UrtFix.Act(pickDrawn, pickShown, pickKeep);
				pickDrawn = (pickShown = null);
			}
			if (!keysLooked)
			{
				keysLooked = true;
				keyRetranslate = DefDatabase<KeyBindingDef>.GetNamedSilentFail("URT_Retranslate");
				keyKeep = DefDatabase<KeyBindingDef>.GetNamedSilentFail("URT_KeepOriginal");
			}
			bool flag = keyRetranslate != null && keyRetranslate.JustPressed;
			bool flag2 = !flag && keyKeep != null && keyKeep.JustPressed;
			if (flag || flag2)
			{
				pickArmed = true;
				pickKeep = flag2;
				pickDrawn = (pickShown = null);
				pickArea = 0f;
			}
			UrtFix.PumpNotes();
		}

		public static void ForgetForDraw(string source, string translation)
		{
			ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
			if (concurrentDictionary != null && !string.IsNullOrEmpty(source))
			{
				concurrentDictionary.TryRemove(Normalize(source), out var value);
				if (!string.IsNullOrEmpty(translation) && concurrentDictionary.TryGetValue(translation, out var value2) && (object)value2 == MarkTarget)
				{
					concurrentDictionary.TryRemove(translation, out value);
				}
				InvalidateAllMemo();
			}
		}

		public static void KeepOriginal(string source)
		{
			ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
			if (concurrentDictionary != null && !string.IsNullOrEmpty(source))
			{
				concurrentDictionary[Normalize(source)] = MarkTarget;
				InvalidateAllMemo();
			}
		}

		public static bool IsKnownTarget(string text)
		{
			ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
			string value;
			return concurrentDictionary != null && !string.IsNullOrEmpty(text) && concurrentDictionary.TryGetValue(text, out value) && (object)value == MarkTarget;
		}

		public static string CurrentDrawTranslation(string source)
		{
			ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
			if (concurrentDictionary != null && !string.IsNullOrEmpty(source) && concurrentDictionary.TryGetValue(source, out var value) && IsTranslation(value))
			{
				return value;
			}
			return null;
		}

		private static void InvalidateAllMemo()
		{
			gen++;
			hitValidFrom = gen;
			missValidFrom = gen;
		}

		private static void InvalidateMissMemo()
		{
			gen++;
			missValidFrom = gen;
		}

		private static bool IsTranslation(string hit)
		{
			return hit != null && (object)hit != MarkTarget && (object)hit != MarkPending;
		}

		public static void PumpDeferredVisible()
		{
			long timestamp = Stopwatch.GetTimestamp();
			try
			{
				PumpDeferredVisibleCore();
			}
			finally
			{
				statPumpTicks += Stopwatch.GetTimestamp() - timestamp;
			}
			LogStats();
		}

		private static void LogStats()
		{
			if (statLines < 10)
			{
				int tickCount = Environment.TickCount;
				if (statNextLog == 0)
				{
					statNextLog = tickCount + 30000;
				}
				else if (tickCount - statNextLog >= 0)
				{
					statNextLog = tickCount + 30000;
					statLines++;
					double num = 1000.0 / (double)Stopwatch.Frequency;
					long num2 = Math.Max(1L, statFrames);
					MLog.Info("[URT] b112 perf 30s: frames=" + statFrames + " labels/frame=" + statCalls / num2 + " memo=" + ((statCalls == 0L) ? 0 : (statMemo * 100 / statCalls)) + "% hits=" + ((statCalls == 0L) ? 0 : (statHits * 100 / statCalls)) + "% draw ms/frame avg=" + ((double)statDrawTicks * num / (double)num2).ToString("0.000") + " max=" + ((double)statDrawMax * num).ToString("0.00") + " overLimit=" + statOverFrames + " | pump ms/frame=" + ((double)statPumpTicks * num / (double)num2).ToString("0.000") + " nudge=" + statNudges + " | liveDone=" + statRegCount + " ms/frame=" + ((double)statRegTicks * num / (double)num2).ToString("0.000") + " pending=" + (UrtLive2.Engaged ? UrtLive2.PendingCount : RuntimeLiveQueue.PendingCount));
					MLog.Info("[URT] b112 live 30s: " + UrtLiveDiag.Line());
					statFrames = (statCalls = (statHits = (statDrawTicks = (statDrawMax = (statOverFrames = 0L)))));
					statPumpTicks = (statNudges = (statRegCount = (statRegTicks = (statMemo = 0L))));
				}
			}
		}

		private static void PumpDeferredVisibleCore()
		{
			UrtLive2.Pump();
			try
			{
				PollFixKeys();
			}
			catch
			{
			}
			UrtHitch.Frame(drawSpent, statRegTicks);
			TakeDrawHandoff();
			statFrames++;
			statDrawTicks += drawSpent;
			if (drawSpent > statDrawMax)
			{
				statDrawMax = drawSpent;
			}
			if (drawOver)
			{
				statOverFrames++;
			}
			drawSpent = 0L;
			drawOver = false;
			handoffCount = 0;
			int tickCount = Environment.TickCount;
			int num = tickCount - lastMissRefresh;
			if ((liveDirty && num >= 1000) || (RuntimeLiveQueue.Active && num >= 10000))
			{
				liveDirty = false;
				lastMissRefresh = tickCount;
				InvalidateMissMemo();
			}
			ReleaseParked();
			int tickCount2 = Environment.TickCount;
			if (tickCount2 - deferredReadyAt < 0)
			{
				return;
			}
			int num2 = 0;
			string result;
			while (num2 < 2 && deferredVisible.TryDequeue(out result))
			{
				num2++;
				try
				{
					RuntimeLiveQueue.EnqueueVisibleForeign(result);
				}
				catch
				{
				}
			}
		}

		private static void TakeDrawHandoff()
		{
			string text = handoff0;
			string text2 = handoff1;
			bool letter = handoff0Letter;
			bool letter2 = handoff1Letter;
			bool flag = drawOver;
			handoff0 = null;
			handoff1 = null;
			if (!flag)
			{
				if (text != null)
				{
					HandToLive(text, letter);
				}
				if (text2 != null)
				{
					HandToLive(text2, letter2);
				}
			}
		}

		private static void HandToLive(string text, bool letter)
		{
			ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
			if (!concurrentDictionary.TryAdd(text, MarkPending))
			{
				return;
			}
			Park(text);
			if (IsTemperatureReadout(text))
			{
				return;
			}
			if (letter)
			{
				RememberAfterLetter(text);
				return;
			}
			statNudges++;
			try
			{
				RuntimeLiveQueue.Nudge(text);
			}
			catch
			{
			}
		}

		private static void Park(string text)
		{
			if (parked.Count >= 4000)
			{
				UnmarkPending(parked.Dequeue().Key);
			}
			parked.Enqueue(new KeyValuePair<string, int>(text, Environment.TickCount + 45000));
		}

		private static void ReleaseParked()
		{
			int tickCount = Environment.TickCount;
			for (int i = 0; i < 16; i++)
			{
				if (parked.Count <= 0)
				{
					break;
				}
				if (tickCount - parked.Peek().Value < 0)
				{
					break;
				}
				UnmarkPending(parked.Dequeue().Key);
			}
		}

		private static void UnmarkPending(string text)
		{
			if (!string.IsNullOrEmpty(text))
			{
				ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
				((ICollection<KeyValuePair<string, string>>)concurrentDictionary).Remove(new KeyValuePair<string, string>(text, MarkPending));
			}
		}

		private static void RememberAfterLetter(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			string text2 = text.Trim();
			if (text2.Length >= 3 && text2.Length <= 8000 && deferredSeen.TryAdd(text2, 1))
			{
				if (deferredSeen.Count > 400)
				{
					deferredSeen.Clear();
				}
				deferredVisible.Enqueue(text2);
				deferredReadyAt = Environment.TickCount + 1500;
				if (!loggedB78)
				{
					loggedB78 = true;
					MLog.Info("[URT] b78 a popping letter is not translated on the frame it appears.");
				}
			}
		}

		private static void EnsureGenSources()
		{
			if (genSources != null)
			{
				return;
			}
			List<ThingDef> allDefsListForReading;
			try
			{
				allDefsListForReading = DefDatabase<ThingDef>.AllDefsListForReading;
			}
			catch
			{
				return;
			}
			if (allDefsListForReading == null || allDefsListForReading.Count == 0)
			{
				return;
			}
			List<GenSource> list = new List<GenSource>();
			Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (ThingDef item in allDefsListForReading)
			{
				if (item == null || string.IsNullOrEmpty(item.label))
				{
					continue;
				}
				if (item.race != null)
				{
					if (item.race.corpseDef != null && item.race.corpseDef.defName == "Corpse_" + item.defName)
					{
						AddGen(list, dictionary, "corpse", item.race.corpseDef, "label", item.label);
						AddGen(list, dictionary, "corpse", item.race.corpseDef, "description", item.label);
					}
					if (item.race.meatDef != null && item.race.meatDef.defName == "Meat_" + item.defName)
					{
						AddGen(list, dictionary, "meat", item.race.meatDef, "label", item.label);
						AddGen(list, dictionary, "meat", item.race.meatDef, "description", item.label);
					}
				}
				AddBuildable(list, dictionary, item);
			}
			try
			{
				foreach (TerrainDef item2 in DefDatabase<TerrainDef>.AllDefsListForReading)
				{
					AddBuildable(list, dictionary, item2);
				}
			}
			catch
			{
			}
			try
			{
				foreach (ResearchProjectDef item3 in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
				{
					if (item3 != null && !string.IsNullOrEmpty(item3.label))
					{
						AddGen(list, dictionary, "techprint", DefDatabase<ThingDef>.GetNamedSilentFail("Techprint_" + item3.defName), "label", item3.label);
					}
				}
			}
			catch
			{
			}
			try
			{
				foreach (AbilityDef item4 in DefDatabase<AbilityDef>.AllDefsListForReading)
				{
					if (item4 != null && !string.IsNullOrEmpty(item4.label))
					{
						AddGen(list, dictionary, "psytrainer", DefDatabase<ThingDef>.GetNamedSilentFail("Psytrainer_" + item4.defName), "label", item4.label);
					}
				}
			}
			catch
			{
			}
			genSources = list;
			List<string> list2 = new List<string>();
			foreach (KeyValuePair<string, int> item5 in dictionary)
			{
				list2.Add(item5.Key + " " + item5.Value);
			}
			MLog.Info("[URT] b108 generated Defs found: " + string.Join(", ", list2.ToArray()) + ".");
		}

		private static void AddBuildable(List<GenSource> list, Dictionary<string, int> counts, BuildableDef b)
		{
			if (b != null && !string.IsNullOrEmpty(b.label))
			{
				AddGen(list, counts, "blueprint", b.blueprintDef, "label", b.label);
				AddGen(list, counts, "blueprint", b.installBlueprintDef, "label", b.label);
				AddGen(list, counts, "frame", b.frameDef, "label", b.label);
			}
		}

		private static void AddGen(List<GenSource> list, Dictionary<string, int> counts, string kind, Def target, string field, string baseLabel)
		{
			if (target != null && !string.IsNullOrEmpty(baseLabel))
			{
				string text = ((field == "label") ? target.label : target.description);
				if (!string.IsNullOrEmpty(text) && WordIndex(text, baseLabel) >= 0)
				{
					list.Add(new GenSource
					{
						Target = target,
						Field = field,
						Text = text,
						BaseLabel = baseLabel
					});
					counts.TryGetValue(kind, out var value);
					counts[kind] = value + 1;
				}
			}
		}

		private static int WordIndex(string text, string word)
		{
			int num = 0;
			while (num <= text.Length - word.Length)
			{
				int num2 = text.IndexOf(word, num, StringComparison.Ordinal);
				if (num2 < 0)
				{
					return -1;
				}
				bool flag = num2 == 0 || !char.IsLetter(text[num2 - 1]);
				int num3 = num2 + word.Length;
				bool flag2 = num3 >= text.Length || !char.IsLetter(text[num3]);
				if (flag && flag2)
				{
					return num2;
				}
				num = num2 + 1;
			}
			return -1;
		}

		private static string ComposeGen(GenSource g, ConcurrentDictionary<string, string> hot)
		{
			if (hot == null || !hot.TryGetValue(g.BaseLabel, out var value) || !IsTranslation(value))
			{
				return null;
			}
			string text = AsLabelPart(value.Trim());
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			int num = WordIndex(g.Text, g.BaseLabel);
			if (num < 0)
			{
				return null;
			}
			return g.Text.Substring(0, num) + text + g.Text.Substring(num + g.BaseLabel.Length);
		}

		private static int AddGeneratedDefs(ConcurrentDictionary<string, string> hot, List<string> targets)
		{
			EnsureGenSources();
			List<GenSource> list = genSources;
			if (list == null)
			{
				return 0;
			}
			int num = 0;
			foreach (GenSource item in list)
			{
				string text = ComposeGen(item, hot);
				if (text != null)
				{
					num += PutGenerated(hot, targets, item.Text, text);
				}
			}
			return num;
		}

		public static int WriteGeneratedIntoDefs()
		{
			if (!UrtMainThread.IsMain)
			{
				UrtMainThread.Post(delegate
				{
					WriteGeneratedIntoDefs();
				});
				return 0;
			}
			ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
			if (concurrentDictionary == null || !GameLanguageIsTarget())
			{
				return 0;
			}
			int num = 0;
			if (genSources != null)
			{
				foreach (GenSource genSource in genSources)
				{
					string text = ComposeGen(genSource, concurrentDictionary);
					if (text == null)
					{
						continue;
					}
					try
					{
						if (genSource.Field == "label")
						{
							genSource.Target.label = text;
						}
						else
						{
							genSource.Target.description = text;
						}
						genSource.Target.ClearCachedData();
						num++;
					}
					catch
					{
					}
				}
			}
			if (recipeSources != null)
			{
				foreach (RecipeSource recipeSource in recipeSources)
				{
					if (recipeSource.Recipe == null)
					{
						continue;
					}
					try
					{
						if (recipeSource.Label != null && concurrentDictionary.TryGetValue(recipeSource.Label, out var value) && IsTranslation(value))
						{
							recipeSource.Recipe.label = value;
							num++;
						}
						if (recipeSource.JobString != null && concurrentDictionary.TryGetValue(recipeSource.JobString, out value) && IsTranslation(value))
						{
							recipeSource.Recipe.jobString = value;
							num++;
						}
						if (recipeSource.Description != null && concurrentDictionary.TryGetValue(recipeSource.Description, out value) && IsTranslation(value))
						{
							recipeSource.Recipe.description = value;
							num++;
						}
						recipeSource.Recipe.ClearCachedData();
					}
					catch
					{
					}
				}
			}
			return num;
		}

		private static void EnsureRecipeSources()
		{
			if (recipeSources != null)
			{
				return;
			}
			List<RecipeDef> allDefsListForReading;
			try
			{
				allDefsListForReading = DefDatabase<RecipeDef>.AllDefsListForReading;
			}
			catch
			{
				return;
			}
			if (allDefsListForReading == null || allDefsListForReading.Count == 0)
			{
				return;
			}
			List<RecipeSource> list = new List<RecipeSource>();
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				try
				{
					RecipeSource recipeSource = ReadGeneratedRecipe(allDefsListForReading[i]);
					if (recipeSource != null)
					{
						list.Add(recipeSource);
					}
				}
				catch
				{
				}
			}
			lowerFirstLabels = CoreLabelsStartLower();
			recipeSources = list;
			MLog.Info("[URT] b89 generated recipes: " + list.Count + " found, composed from the language templates.");
		}

		private static bool GameLanguageIsTarget()
		{
			try
			{
				LoadedLanguage activeLanguage = LanguageDatabase.activeLanguage;
				if (activeLanguage == null)
				{
					return false;
				}
				string b = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
				return string.Equals(LanguageCodeMapper.Canonical(activeLanguage.folderName), b, StringComparison.OrdinalIgnoreCase) || string.Equals(LanguageCodeMapper.Canonical(activeLanguage.LegacyFolderName), b, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static RecipeSource ReadGeneratedRecipe(RecipeDef r)
		{
			if (r == null || r.products == null || r.products.Count != 1)
			{
				return null;
			}
			ThingDefCountClass thingDefCountClass = r.products[0];
			ThingDef thingDef = thingDefCountClass?.thingDef;
			if (thingDef == null || thingDef.recipeMaker == null || string.IsNullOrEmpty(thingDef.label))
			{
				return null;
			}
			int adjustedCount = r.adjustedCount;
			string b = "Make_" + thingDef.defName + ((adjustedCount != 1) ? "Bulk" : "");
			if (!string.Equals(r.defName, b, StringComparison.Ordinal))
			{
				return null;
			}
			string text = thingDef.label + ((adjustedCount != 1) ? (" x" + adjustedCount) : "");
			RecipeSource recipeSource = new RecipeSource
			{
				ProductLabel = thingDef.label,
				Count = adjustedCount,
				Recipe = r
			};
			if (string.IsNullOrEmpty(thingDef.recipeMaker.label))
			{
				string text2 = "RecipeMake".Translate(text);
				if (string.Equals(r.label, text2, StringComparison.Ordinal))
				{
					recipeSource.Label = text2;
				}
			}
			string text3 = "RecipeMakeJobString".Translate(text);
			if (string.Equals(r.jobString, text3, StringComparison.Ordinal))
			{
				recipeSource.JobString = text3;
			}
			string text4 = ((thingDefCountClass.count != 1) ? thingDefCountClass.Label : Find.ActiveLanguageWorker.WithIndefiniteArticle(thingDef.label));
			string text5 = "RecipeMakeDescription".Translate(text4);
			if (string.Equals(r.description, text5, StringComparison.Ordinal) && text4.IndexOf(thingDef.label, StringComparison.Ordinal) >= 0)
			{
				recipeSource.Description = text5;
				recipeSource.ItemText = text4;
			}
			if (recipeSource.Label == null && recipeSource.JobString == null && recipeSource.Description == null)
			{
				return null;
			}
			return recipeSource;
		}

		private static bool CoreLabelsStartLower()
		{
			int num = 0;
			int num2 = 0;
			try
			{
				foreach (ThingDef item in DefDatabase<ThingDef>.AllDefsListForReading)
				{
					if (item == null || item.modContentPack == null || !item.modContentPack.IsCoreMod)
					{
						continue;
					}
					string label = item.label;
					if (!string.IsNullOrEmpty(label) && char.IsLetter(label[0]))
					{
						if (char.IsLower(label[0]))
						{
							num++;
							num2++;
						}
						else if (char.IsUpper(label[0]))
						{
							num2++;
						}
						if (num2 >= 400)
						{
							break;
						}
					}
				}
			}
			catch
			{
			}
			return num2 > 0 && num * 10 >= num2 * 7;
		}

		private static string AsLabelPart(string translated)
		{
			if (!lowerFirstLabels || translated == null || translated.Length < 2)
			{
				return translated;
			}
			if (char.IsUpper(translated[0]) && char.IsLower(translated[1]))
			{
				return char.ToLowerInvariant(translated[0]) + translated.Substring(1);
			}
			return translated;
		}

		private static void AddActiveLanguageTexts(List<string> targets)
		{
			if (activeLanguageTexts == null)
			{
				if (!GameLanguageIsTarget())
				{
					return;
				}
				LoadedLanguage activeLanguage = LanguageDatabase.activeLanguage;
				if (activeLanguage == null || activeLanguage.keyedReplacements == null || activeLanguage.keyedReplacements.Count == 0)
				{
					return;
				}
				List<string> list = new List<string>(activeLanguage.keyedReplacements.Count * 2);
				foreach (KeyValuePair<string, LoadedLanguage.KeyedReplacement> keyedReplacement in activeLanguage.keyedReplacements)
				{
					LoadedLanguage.KeyedReplacement value = keyedReplacement.Value;
					if (value != null && !value.isPlaceholder && !string.IsNullOrWhiteSpace(value.value) && value.value.Length >= 3)
					{
						list.Add(value.value);
					}
				}
				if (activeLanguage.defInjections != null)
				{
					foreach (DefInjectionPackage defInjection in activeLanguage.defInjections)
					{
						if (defInjection == null || defInjection.injections == null)
						{
							continue;
						}
						foreach (KeyValuePair<string, DefInjectionPackage.DefInjection> injection in defInjection.injections)
						{
							DefInjectionPackage.DefInjection value2 = injection.Value;
							if (value2 != null && !value2.isPlaceholder && !string.IsNullOrWhiteSpace(value2.injection) && value2.injection.Length >= 3)
							{
								list.Add(value2.injection);
							}
						}
					}
				}
				activeLanguageTexts = list;
				MLog.Info("[URT] b95 " + list.Count + " texts of the active language marked as already translated.");
			}
			targets.AddRange(activeLanguageTexts);
		}

		private static int AddGeneratedRecipes(ConcurrentDictionary<string, string> hot, List<string> targets)
		{
			EnsureRecipeSources();
			List<RecipeSource> list = recipeSources;
			if (list == null)
			{
				return 0;
			}
			if (!GameLanguageIsTarget())
			{
				if (!loggedRecipeLanguage)
				{
					loggedRecipeLanguage = true;
					MLog.Info("[URT] b89 generated recipes skipped: game language differs from the URT target language.");
				}
				return 0;
			}
			int num = 0;
			for (int i = 0; i < list.Count; i++)
			{
				RecipeSource recipeSource = list[i];
				if (!hot.TryGetValue(recipeSource.ProductLabel, out var value) || !IsTranslation(value))
				{
					continue;
				}
				string text = AsLabelPart(value.Trim());
				if (string.IsNullOrEmpty(text))
				{
					continue;
				}
				try
				{
					string text2 = text + ((recipeSource.Count != 1) ? (" x" + recipeSource.Count) : "");
					if (recipeSource.Label != null)
					{
						num += PutGenerated(hot, targets, recipeSource.Label, "RecipeMake".Translate(text2));
					}
					if (recipeSource.JobString != null)
					{
						num += PutGenerated(hot, targets, recipeSource.JobString, "RecipeMakeJobString".Translate(text2));
					}
					if (recipeSource.Description != null)
					{
						string text3 = ((recipeSource.Count != 1) ? recipeSource.ItemText.Replace(recipeSource.ProductLabel, text) : Find.ActiveLanguageWorker.WithIndefiniteArticle(text));
						num += PutGenerated(hot, targets, recipeSource.Description, "RecipeMakeDescription".Translate(text3));
					}
				}
				catch
				{
				}
			}
			return num;
		}

		private static int PutGenerated(ConcurrentDictionary<string, string> hot, List<string> targets, string source, string composed)
		{
			if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(composed))
			{
				return 0;
			}
			string text = Normalize(source);
			string text2 = composed.Trim();
			if (!KeepForDraw(text, text2))
			{
				return 0;
			}
			hot[text] = text2;
			targets.Add(text2);
			return 1;
		}

		private static bool KeepForDraw(string source, string hit)
		{
			if (string.IsNullOrEmpty(hit) || string.IsNullOrEmpty(source))
			{
				return false;
			}
			if (string.Equals(source, hit, StringComparison.Ordinal))
			{
				return false;
			}
			if (!targetIsCzech)
			{
				return true;
			}
			return !HasCzMark(source) || HasCzMark(hit);
		}

		private static bool HasCzMark(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			foreach (char c in text)
			{
				if (c == 'á' || c == 'č' || c == 'ď' || c == 'é' || c == 'ě' || c == 'í' || c == 'ň' || c == 'ó' || c == 'ř' || c == 'š' || c == 'ť' || c == 'ú' || c == 'ů' || c == 'ý' || c == 'ž' || c == 'Á' || c == 'Č' || c == 'Ď' || c == 'É' || c == 'Ě' || c == 'Í' || c == 'Ň' || c == 'Ó' || c == 'Ř' || c == 'Š' || c == 'Ť' || c == 'Ú' || c == 'Ů' || c == 'Ý' || c == 'Ž')
				{
					return true;
				}
			}
			return false;
		}

		private static bool IsTemperatureReadout(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			bool flag = false;
			bool flag2 = false;
			foreach (char c in text)
			{
				if (char.IsDigit(c))
				{
					flag = true;
				}
				else if (c == 'C' || c == 'c' || c == 'F' || c == 'f' || c == '°')
				{
					flag2 = true;
				}
				else if (char.IsLetter(c))
				{
					return false;
				}
			}
			return flag && flag2;
		}

		public static void ReleaseMiss(string text)
		{
			if (!string.IsNullOrWhiteSpace(text))
			{
				UnmarkPending(text);
			}
		}

		public static void RegisterLiveTrusted(string source, string translation)
		{
			if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation))
			{
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			lock (Gate)
			{
				string text = Normalize(source);
				string text2 = translation.Trim();
				string targetLanguage = UniversalRimTranslatorSettings.PlayerTargetLanguage();
				translations[RuntimeKey(source, null, targetLanguage)] = text2;
				ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
				if (concurrentDictionary != null && KeepForDraw(text, text2))
				{
					concurrentDictionary[text] = text2;
					liveDirty = true;
					if (!concurrentDictionary.TryGetValue(text2, out var value) || (object)value == MarkPending)
					{
						concurrentDictionary[text2] = MarkTarget;
					}
				}
			}
			statRegCount++;
			statRegTicks += Stopwatch.GetTimestamp() - timestamp;
		}

		public static void RegisterLive(string source, string translation)
		{
			if (!UnityData.IsInMainThread)
			{
				RegisterLiveCore(source, translation);
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			try
			{
				RegisterLiveCore(source, translation);
			}
			finally
			{
				statRegCount++;
				statRegTicks += Stopwatch.GetTimestamp() - timestamp;
			}
		}

		private static void RegisterLiveCore(string source, string translation)
		{
			if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation) || translation.IndexOf("LANGPAIR", StringComparison.OrdinalIgnoreCase) >= 0 || translation.IndexOf("INVALID SOURCE LANGUAGE", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return;
			}
			lock (Gate)
			{
				string text = Normalize(source);
				string text2 = UrtGlossary.After(translation);
				if (string.IsNullOrWhiteSpace(text2) || RuntimeLiveQueue.HasForeignRun(text2) || LanguageDetector.IsHalfTranslated(text2, UniversalRimTranslatorSettings.PlayerTargetLanguage()))
				{
					UrtLiveDiag.Outcome("droppedOnApply");
					return;
				}
				string targetLanguage = UniversalRimTranslatorSettings.PlayerTargetLanguage();
				translations[RuntimeKey(source, null, targetLanguage)] = text2;
				AddTextTargetFallback(source, targetLanguage, text2);
				ConcurrentDictionary<string, string> concurrentDictionary = hotExact;
				if (concurrentDictionary != null && KeepForDraw(text, text2))
				{
					concurrentDictionary[text] = text2;
					liveDirty = true;
					if (!concurrentDictionary.TryGetValue(text2, out var value) || (object)value == MarkPending)
					{
						concurrentDictionary[text2] = MarkTarget;
					}
				}
				if (text.Length >= 18 && text.IndexOf(' ') >= 0 && longPhrases.Count < 80)
				{
					longPhrases.Add(new KeyValuePair<string, string>(text, text2));
				}
			}
		}

		private static void Collect(Type type, BindingFlags flags, List<MethodInfo> targets)
		{
			if (type == null || IsUnsafeLiveType(type))
			{
				return;
			}
			try
			{
				MethodInfo[] methods = type.GetMethods(flags);
				foreach (MethodInfo item in methods)
				{
					targets.Add(item);
				}
			}
			catch
			{
			}
		}

		private static bool IsUnsafeLiveType(Type type)
		{
			string text = type.FullName ?? type.Name ?? "";
			int result;
			switch (text)
			{
			default:
				result = ((text == "Verse.PlayLog") ? 1 : 0);
				break;
			case "UnityEngine.GUI":
			case "Verse.Translator":
			case "Verse.LanguageDatabase":
			case "Verse.Def":
			case "Verse.ThingDef":
			case "Verse.Thing":
			case "Verse.Hediff":
			case "Verse.HediffDef":
				result = 1;
				break;
			}
			return (byte)result != 0;
		}

		private static bool TryPhrase(string original, out string replaced)
		{
			replaced = original;
			if (string.IsNullOrEmpty(original))
			{
				return false;
			}
			if (!string.Equals(UniversalRimTranslatorSettings.PlayerTargetLanguage(), "Czech", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			foreach (KeyValuePair<string, string> phraseFallback in PhraseFallbacks)
			{
				if (string.Equals(original, phraseFallback.Key, StringComparison.OrdinalIgnoreCase))
				{
					replaced = phraseFallback.Value;
					return true;
				}
			}
			string text = original;
			bool flag = false;
			for (int i = 0; i < 3; i++)
			{
				bool flag2 = false;
				foreach (KeyValuePair<string, string> phraseFallback2 in PhraseFallbacks)
				{
					string key = phraseFallback2.Key;
					if (text.Length <= key.Length || !text.StartsWith(key, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					char c = text[key.Length];
					bool flag3 = key.EndsWith(")") && char.IsLetter(c);
					if (!flag3 && c != ' ' && c != ':' && c != '-')
					{
						continue;
					}
					string text2 = (flag3 ? text.Substring(key.Length) : text.Substring(key.Length).TrimStart(Array.Empty<char>()));
					if (!TailIsNameOnly(text2) && !StartsWithPhrase(text2))
					{
						continue;
					}
					if (StartsWithPhrase(text2) && TryPhrase(text2, out var replaced2))
					{
						text2 = replaced2;
					}
					string text3 = ((flag3 || c == ' ') ? " " : "");
					text = phraseFallback2.Value + text3 + text2;
					flag = true;
					flag2 = true;
					break;
				}
				if (!flag2)
				{
					break;
				}
			}
			if (flag && !LanguageDetector.HasResidualEnglish(text, "Czech"))
			{
				replaced = text;
				return true;
			}
			return false;
		}

		private static bool StartsWithPhrase(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			foreach (KeyValuePair<string, string> phraseFallback in PhraseFallbacks)
			{
				if (text.StartsWith(phraseFallback.Key, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static bool TailIsNameOnly(string tail)
		{
			if (string.IsNullOrWhiteSpace(tail))
			{
				return true;
			}
			string[] array = tail.Split(new char[6] { ' ', ':', ',', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
			if (array.Length == 0)
			{
				return true;
			}
			for (int i = 0; i < array.Length; i++)
			{
				if (LanguageDetector.HasResidualEnglish(array[i], "Czech"))
				{
					return false;
				}
				bool flag = true;
				int num = 0;
				for (int j = 0; j < array[i].Length; j++)
				{
					if (char.IsLetter(array[i][j]))
					{
						num++;
						if (char.IsUpper(array[i][j]))
						{
							flag = false;
						}
					}
				}
				if (flag && num >= 4)
				{
					return false;
				}
			}
			return true;
		}

		private static bool TryReplace(string original, out string replaced)
		{
			replaced = original;
			string text = Normalize(original);
			string key = RuntimeKey(text);
			bool flag;
			string value;
			lock (Gate)
			{
				flag = translations.TryGetValue(key, out value);
			}
			if (flag)
			{
				string text2 = PolishHit(value);
				if (RuntimeLiveQueue.HasForeignRun(text2))
				{
					bool flag2;
					string replaced2;
					lock (Gate)
					{
						flag2 = ApplyKnownForeignRuns(text2, out replaced2);
					}
					if (flag2)
					{
						text2 = PolishHit(replaced2);
					}
				}
				replaced = text2;
				return !RuntimeLiveQueue.HasForeignRun(text2) && !LanguageDetector.IsHalfTranslated(text2, UniversalRimTranslatorSettings.PlayerTargetLanguage());
			}
			bool flag3;
			string replaced3;
			lock (Gate)
			{
				flag3 = ApplyKnownForeignRuns(original, out replaced3);
			}
			if (flag3)
			{
				replaced = replaced3;
				return true;
			}
			if (string.Equals(UniversalRimTranslatorSettings.PlayerTargetLanguage(), "Czech", StringComparison.OrdinalIgnoreCase))
			{
				foreach (KeyValuePair<string, string> phraseFallback in PhraseFallbacks)
				{
					if (string.Equals(text, phraseFallback.Key, StringComparison.OrdinalIgnoreCase))
					{
						replaced = phraseFallback.Value;
						return true;
					}
				}
			}
			return false;
		}

		private static bool ApplyKnownForeignRuns(string original, out string replaced)
		{
			replaced = original;
			if (string.IsNullOrEmpty(original) || !RuntimeLiveQueue.HasForeignRun(original))
			{
				return false;
			}
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = new StringBuilder();
			bool flag = false;
			for (int i = 0; i <= original.Length; i++)
			{
				if (i < original.Length && IsCjkLetter(original[i]))
				{
					stringBuilder2.Append(original[i]);
					continue;
				}
				if (stringBuilder2.Length > 0)
				{
					string text = stringBuilder2.ToString();
					if (text.Length >= 4 && translations.TryGetValue(text, out var value) && !string.IsNullOrEmpty(value))
					{
						stringBuilder.Append(value);
						flag = true;
					}
					else
					{
						stringBuilder.Append(text);
					}
					stringBuilder2.Length = 0;
				}
				if (i < original.Length)
				{
					stringBuilder.Append(original[i]);
				}
			}
			if (!flag)
			{
				return false;
			}
			replaced = stringBuilder.ToString();
			return true;
		}

		private static bool IsCjkLetter(char c)
		{
			if (c >= '\u3040' && c <= 'ヿ')
			{
				return true;
			}
			if (c >= '㐀' && c <= '\u9fff')
			{
				return true;
			}
			if (c >= '가' && c <= '\ud7af')
			{
				return true;
			}
			return false;
		}

		private static bool IsSupported(MethodInfo method)
		{
			if (method == null || method.IsGenericMethodDefinition || method.IsAbstract)
			{
				return false;
			}
			string text = method.Name ?? "";
			int num;
			switch (text)
			{
			default:
				num = ((text == "Message") ? 1 : 0);
				break;
			case "Label":
			case "LabelCap":
			case "LabelFitted":
			case "LabelScrollable":
			case "LabelWithIcon":
			case "CheckboxLabeled":
			case "RadioButtonLabeled":
			case "ButtonText":
			case "ButtonTextSubtle":
			case "ButtonTextLabeled":
			case "ButtonTextLabeledPct":
			case "TipRegion":
			case "TipRegionByKey":
			case "DrawHighlightIfMouseover":
			case "get_Title":
			case "get_TitleRaw":
			case "get_Label":
			case "get_LabelCap":
			case "get_LabelShort":
			case "get_Description":
			case "get_Desc":
			case "get_HelpText":
			case "get_Tip":
			case "get_ActiveTip":
			case "get_Explanation":
			case "get_JobString":
			case "get_LetterText":
			case "get_Text":
			case "get_Caption":
			case "GetInspectString":
			case "GetInspectStringLowPriority":
			case "GetTooltip":
			case "GetTip":
			case "TipString":
			case "FullDescription":
			case "GetDescription":
			case "GetLabel":
			case "GetLabelCap":
			case "Translate":
			case "TranslateKeyed":
			case "ToStringFull":
			case "ToStringShort":
				num = 1;
				break;
			}
			bool flag = (byte)num != 0;
			if (!flag && (IsStringGetter(text) || IsStringInspect(text)))
			{
				flag = true;
			}
			if (!flag)
			{
				return false;
			}
			ParameterInfo[] parameters = method.GetParameters();
			if ((IsStringGetter(text) || IsStringInspect(text) || text == "Translate" || text == "TranslateKeyed") && (method.ReturnType == typeof(string) || method.ReturnType.FullName == "Verse.TaggedString"))
			{
				return true;
			}
			if (parameters.Length >= 2 && parameters[0].ParameterType == typeof(Rect) && parameters[1].ParameterType.FullName == "Verse.TaggedString")
			{
				return true;
			}
			if (parameters.Length >= 2 && parameters[0].ParameterType == typeof(Rect) && parameters[1].ParameterType == typeof(string) && !parameters[1].ParameterType.IsByRef)
			{
				return true;
			}
			if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(string) && !parameters[0].ParameterType.IsByRef)
			{
				return true;
			}
			return false;
		}

		private static string CheapCleanup(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}
			if (text.IndexOf('\\') < 0 && text.IndexOf("URT", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("ideoligion", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return text;
			}
			return CleanupRuntime(text);
		}

		private static string CleanupRuntime(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}
			text = text.Replace("\\r\\n", "\n").Replace("\\N", "\n").Replace("\\n", "\n");
			text = text.Replace("\\ n", "\n").Replace("\\  n", "\n");
			text = Regex.Replace(text, "\\\\[ \\t]*n", "\n");
			if (string.Equals(UniversalRimTranslatorSettings.PlayerTargetLanguage(), "Czech", StringComparison.OrdinalIgnoreCase))
			{
				text = text.Replace("ideoligiony", "ideobožství");
				text = text.Replace("Ideoligiony", "Ideobožství");
				text = text.Replace("ideoligion", "ideobožství");
				text = text.Replace("Ideoligion", "Ideobožství");
				text = text.Replace("Zatížení uloženo", "Načítají se uložené");
				text = text.Replace("zástupný symbol", "").Replace("zastupny symbol", "");
			}
			text = UrtMask.Replace(text, "{$1}");
			text = Regex.Replace(text, "[_ ]*URT[_ ]*", " ", RegexOptions.IgnoreCase);
			while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
			{
				text = text.Replace("  ", " ");
			}
			return text.Trim();
		}

		private static bool IsStringGetter(string name)
		{
			int result;
			switch (name)
			{
			default:
				result = ((name == "get_Caption") ? 1 : 0);
				break;
			case "get_Title":
			case "get_TitleRaw":
			case "get_Label":
			case "get_LabelCap":
			case "get_LabelShort":
			case "get_Description":
			case "get_Desc":
			case "get_HelpText":
			case "get_Tip":
			case "get_ActiveTip":
			case "get_Explanation":
			case "get_JobString":
			case "get_LetterText":
			case "get_Text":
				result = 1;
				break;
			}
			return (byte)result != 0;
		}

		private static bool IsStringInspect(string name)
		{
			int result;
			switch (name)
			{
			default:
				result = ((name == "GetLabelCap") ? 1 : 0);
				break;
			case "GetInspectString":
			case "GetInspectStringLowPriority":
			case "GetTooltip":
			case "GetTip":
			case "TipString":
			case "FullDescription":
			case "GetDescription":
			case "GetLabel":
				result = 1;
				break;
			}
			return (byte)result != 0;
		}

		private static string Normalize(string value)
		{
			return (value ?? "").Trim().Replace("\r\n", "\n").Replace("\r", "\n");
		}
	}
	internal sealed class UrtDrawKeyComparer : IEqualityComparer<string>
	{
		public static readonly UrtDrawKeyComparer Instance = new UrtDrawKeyComparer();

		public bool Equals(string a, string b)
		{
			if ((object)a == b)
			{
				return true;
			}
			if (a == null || b == null)
			{
				return false;
			}
			Bounds(a, out var start, out var end);
			Bounds(b, out var start2, out var end2);
			int num;
			do
			{
				num = Next(a, ref start, end);
				int num2 = Next(b, ref start2, end2);
				if (num != num2)
				{
					return false;
				}
			}
			while (num >= 0);
			return true;
		}

		public int GetHashCode(string s)
		{
			if (s == null)
			{
				return 0;
			}
			Bounds(s, out var start, out var end);
			int num = -2128831035;
			int num2;
			while ((num2 = Next(s, ref start, end)) >= 0)
			{
				num = (num ^ num2) * 16777619;
			}
			return num;
		}

		private static void Bounds(string s, out int start, out int end)
		{
			start = 0;
			end = s.Length;
			while (start < end && char.IsWhiteSpace(s[start]))
			{
				start++;
			}
			while (end > start && char.IsWhiteSpace(s[end - 1]))
			{
				end--;
			}
		}

		private static int Next(string s, ref int i, int end)
		{
			if (i >= end)
			{
				return -1;
			}
			char c = s[i++];
			if (c == '\r')
			{
				if (i < end && s[i] == '\n')
				{
					i++;
				}
				return 10;
			}
			if (c < '\u0080')
			{
				return (c >= 'a' && c <= 'z') ? (c - 32) : c;
			}
			return char.ToUpperInvariant(c);
		}
	}
	public sealed class SimplyTranslateAiProvider : IUniversalTranslationProvider
	{
		private readonly HttpClient client;

		public string Name => "SimplyTranslate";

		public SimplyTranslateAiProvider()
		{
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_0013: Expected O, but got Unknown
			client = new HttpClient();
			client.Timeout = TimeSpan.FromSeconds(20.0);
			((HttpHeaders)client.DefaultRequestHeaders).TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128 Safari/537.36");
			((HttpHeaders)client.DefaultRequestHeaders).TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
		}

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Batch is empty.";
				return result;
			}
			string sourceCode = Code(source);
			string targetCode = Code(target);
			string[] translations = new string[texts.Count];
			string[] errors = new string[texts.Count];
			for (int start = 0; start < texts.Count; start += 10)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int count = Math.Min(10, texts.Count - start);
				List<Task> tasks = new List<Task>();
				for (int j = 0; j < count; j++)
				{
					int index = start + j;
					tasks.Add(TranslateOneAsync(texts[index] ?? "", sourceCode, targetCode, settings, cancellationToken, delegate(string value)
					{
						translations[index] = value;
					}, delegate(string error)
					{
						errors[index] = error;
					}, delegate(int code)
					{
						result.StatusCode = code;
					}));
				}
				await Task.WhenAll(tasks);
				for (int k = 0; k < count; k++)
				{
					int index2 = start + k;
					if (string.IsNullOrWhiteSpace(translations[index2]) && string.IsNullOrWhiteSpace(errors[index2]))
					{
						errors[index2] = "SimplyTranslate AI endpoint failed.";
					}
				}
			}
			for (int l = 0; l < translations.Length; l++)
			{
				result.Translations.Add(translations[l] ?? "");
				result.CharacterCount += (texts[l] ?? "").Length;
			}
			result.Success = result.Translations.Count == texts.Count && result.Translations.TrueForAll((string x) => !string.IsNullOrWhiteSpace(x));
			if (!result.Success)
			{
				for (int i = 0; i < errors.Length; i++)
				{
					if (!string.IsNullOrWhiteSpace(errors[i]))
					{
						result.Error = "SimplyTranslate failed at item " + (i + 1) + "/" + texts.Count + ": " + errors[i];
						break;
					}
				}
				if (string.IsNullOrWhiteSpace(result.Error))
				{
					result.Error = "SimplyTranslate returned incomplete translations.";
				}
			}
			return result;
		}

		private async Task TranslateOneAsync(string input, string sourceCode, string targetCode, TranslationProviderSettings settings, CancellationToken cancellationToken, Action<string> setTranslation, Action<string> setError, Action<int> setStatusCode)
		{
			try
			{
				string endpoint = settings?.SimplyTranslateEndpoint;
				if (string.IsNullOrWhiteSpace(endpoint))
				{
					return;
				}
				string json = "{\"text\":" + JsonEscape(input) + ",\"from\":" + JsonEscape(sourceCode) + ",\"to\":" + JsonEscape(targetCode) + "}";
				HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint.Trim());
				try
				{
					request.Content = (HttpContent)new StringContent(json, Encoding.UTF8, "application/json");
					HttpResponseMessage response = await ((HttpMessageInvoker)client).SendAsync(request, cancellationToken);
					try
					{
						setStatusCode((int)response.StatusCode);
						string body = await response.Content.ReadAsStringAsync();
						if (response.IsSuccessStatusCode)
						{
							string translated = ProviderJsonHelper.ExtractFirstString(body, "result");
							if (!string.IsNullOrWhiteSpace(translated))
							{
								setTranslation(WebUtility.HtmlDecode(translated));
								return;
							}
						}
						setError("AI endpoint HTTP " + (int)response.StatusCode + ".");
					}
					finally
					{
						((IDisposable)response)?.Dispose();
					}
				}
				finally
				{
					((IDisposable)request)?.Dispose();
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex3)
			{
				Exception ex = ex3;
				setError("AI endpoint " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private static string JsonEscape(string value)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append('"');
			string text = value ?? "";
			foreach (char c in text)
			{
				switch (c)
				{
				case '\\':
					stringBuilder.Append("\\\\");
					continue;
				case '"':
					stringBuilder.Append("\\\"");
					continue;
				case '\r':
					stringBuilder.Append("\\r");
					continue;
				case '\n':
					stringBuilder.Append("\\n");
					continue;
				case '\t':
					stringBuilder.Append("\\t");
					continue;
				}
				if (c < ' ')
				{
					int num = c;
					stringBuilder.Append("\\u" + num.ToString("x4"));
				}
				else
				{
					stringBuilder.Append(c);
				}
			}
			stringBuilder.Append('"');
			return stringBuilder.ToString();
		}

		private static string Code(string language)
		{
			switch ((language ?? "").Trim().ToLowerInvariant())
			{
			case "english":
				return "en";
			case "czech":
				return "cs";
			case "german":
				return "de";
			case "french":
				return "fr";
			case "italian":
				return "it";
			case "japanese":
				return "ja";
			case "korean":
				return "ko";
			case "polish":
				return "pl";
			case "portuguese":
			case "portuguesebrazilian":
				return "pt";
			case "russian":
				return "ru";
			case "spanish":
				return "es";
			case "dutch":
				return "nl";
			case "danish":
				return "da";
			case "swedish":
				return "sv";
			case "norwegian":
				return "no";
			case "ukrainian":
				return "uk";
			case "chinesesimplified":
				return "zh-CN";
			case "chinesetraditional":
				return "zh-TW";
			case "chinese":
				return "zh-CN";
			case "turkish":
				return "tr";
			case "vietnamese":
				return "vi";
			case "arabic":
				return "ar";
			case "bulgarian":
				return "bg";
			case "croatian":
				return "hr";
			case "finnish":
				return "fi";
			case "greek":
				return "el";
			case "hebrew":
				return "he";
			case "hungarian":
				return "hu";
			case "indonesian":
				return "id";
			case "romanian":
				return "ro";
			case "slovak":
				return "sk";
			case "slovenian":
				return "sl";
			case "thai":
				return "th";
			case "estonian":
				return "et";
			case "latvian":
				return "lv";
			case "lithuanian":
				return "lt";
			default:
			{
				string text = (language ?? "").Trim().ToLowerInvariant();
				return string.IsNullOrEmpty(text) ? "auto" : text;
			}
			}
		}
	}
	public sealed class BatchProgress
	{
		public bool Running;

		public bool Finished;

		public int TotalCandidates;

		public int Processed;

		public int Translated;

		public int FallbackTranslated;

		public int CachedSkipped;

		public int Failed;

		public int CharactersSent;

		public int CurrentBatchSize;

		public string CurrentProvider = "";

		public string Status = "Ready.";

		public readonly Dictionary<string, int> ProviderTranslated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		public readonly Dictionary<string, int> ProviderFailed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		public readonly Dictionary<string, string> ProviderLastError = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public readonly object StatsLock = new object();
	}
	public sealed class TranslationBatchManager
	{
		private sealed class ProtectedTextBatch
		{
			public readonly List<string> Texts = new List<string>();

			public readonly List<Dictionary<string, string>> Maps = new List<Dictionary<string, string>>();
		}

		private sealed class ParallelProviderBatchResult
		{
			public ProviderAdapter Provider;

			public List<TranslationCandidate> Candidates = new List<TranslationCandidate>();

			public List<string> UniqueTexts = new List<string>();

			public TranslationProviderResult Response;
		}

		private sealed class ProviderAdapter
		{
			public readonly string Name;

			private readonly Func<IList<string>, string, string, TranslationProviderSettings, CancellationToken, Task<TranslationProviderResult>> func;

			public ProviderAdapter(string name, Func<IList<string>, string, string, TranslationProviderSettings, CancellationToken, Task<TranslationProviderResult>> func)
			{
				Name = name;
				this.func = func;
			}

			public Task<TranslationProviderResult> TranslateAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken token)
			{
				return func(texts, source, target, settings, token);
			}
		}

		private readonly List<TranslationCandidate> lastFailedCandidates = new List<TranslationCandidate>();

		private const int ProviderBatchSize = 200;

		private const int ProviderBatchMaxChars = 49500;

		private const int ParallelProviderSlots = 4;

		private const int ParallelChunkSize = 120;

		private static readonly HashSet<string> RunSkipProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static readonly HashSet<string> EchoedByFast = new HashSet<string>(StringComparer.Ordinal);

		private static bool loggedB71;

		private static readonly ConcurrentDictionary<string, int> providerFailLog = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		private static int judgeSamples;

		private static readonly HashSet<string> SampledSources = new HashSet<string>(StringComparer.Ordinal);

		public BatchProgress Progress { get; private set; }

		public IReadOnlyList<TranslationCandidate> LastFailedCandidates => lastFailedCandidates;

		private static void NoteEcho(string providerName, string source, string translated)
		{
			if (!IsFastBatchProvider(providerName) || string.IsNullOrWhiteSpace(translated) || !string.Equals(NormalizeComparisonText(source), NormalizeComparisonText(translated), StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			lock (EchoedByFast)
			{
				EchoedByFast.Add(source ?? "");
			}
		}

		private static bool WasEchoed(string source)
		{
			lock (EchoedByFast)
			{
				return EchoedByFast.Contains(source ?? "");
			}
		}

		public static void ClearRunSkips()
		{
			RunSkipProviders.Clear();
			lock (EchoedByFast)
			{
				EchoedByFast.Clear();
			}
		}

		public async Task TranslateNextBatchAsync(IList<TranslationCandidate> candidates, TranslationCache cache, DeepLProvider deepL, GoogleTranslateProvider googleCloud, string deepLApiKey, string googleApiKey, string targetLanguage, int requestedAmount, CancellationToken cancellationToken, TranslationProviderSettings providerSettings = null, MyMemoryTranslateProvider myMemory = null, SimplyTranslateAiProvider simplyTranslate = null, YandexTranslateProvider yandex = null, bool forceIgnoreCache = false)
		{
			if (Progress != null && Progress.Running)
			{
				throw new InvalidOperationException("A translation batch is already running.");
			}
			providerSettings = providerSettings ?? new TranslationProviderSettings();
			providerSettings.DeepLApiKey = deepLApiKey ?? providerSettings.DeepLApiKey ?? "";
			providerSettings.GoogleCloudApiKey = googleApiKey ?? providerSettings.GoogleCloudApiKey ?? "";
			List<TranslationCandidate> selected = Enumerable.Take(count: Math.Max(1, Math.Min(20000, requestedAmount)), source: candidates ?? new List<TranslationCandidate>()).ToList();
			lastFailedCandidates.Clear();
			Progress = new BatchProgress
			{
				Running = true,
				Finished = false,
				TotalCandidates = selected.Count,
				Status = ((selected.Count == 0) ? "Nothing to translate." : ("Checking cache for " + selected.Count + " texts…"))
			};
			try
			{
				foreach (TranslationCandidate candidate in selected)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (!forceIgnoreCache && cache.TryGet(candidate.ModPackageId, candidate.Type, candidate.DefType, candidate.Key, targetLanguage, out var cached) && IsUsefulCache(cached, candidate.SourceText))
					{
						Progress.CachedSkipped++;
						Progress.Processed++;
					}
					else
					{
						cached = null;
					}
				}
				List<TranslationCandidate> pending = new List<TranslationCandidate>();
				foreach (TranslationCandidate candidate2 in selected)
				{
					if (forceIgnoreCache || !cache.TryGet(candidate2.ModPackageId, candidate2.Type, candidate2.DefType, candidate2.Key, targetLanguage, out var cached2) || !IsUsefulCache(cached2, candidate2.SourceText))
					{
						string packHit;
						string ram;
						if (SkipForSpeed(candidate2))
						{
							Progress.CachedSkipped++;
							Progress.Processed++;
						}
						else if (LanguageDetector.LooksLikeTargetText(candidate2.SourceText, targetLanguage) && !LanguageDetector.IsHalfTranslated(candidate2.SourceText, targetLanguage) && !LanguageDetector.HasLeftoverLatin(candidate2.SourceText) && !LanguageDetector.HasResidualForeignScript(candidate2.SourceText, targetLanguage) && TranslationCache.IsGoodStored(candidate2.SourceText, candidate2.SourceText, targetLanguage))
						{
							cache.Put(new CachedTranslation
							{
								ModName = candidate2.ModName,
								ModPackageId = candidate2.ModPackageId,
								Type = candidate2.Type,
								DefType = candidate2.DefType,
								Key = candidate2.Key,
								SourceLanguage = LanguageDetector.DetectEmbeddedSourceLanguage(candidate2.SourceText),
								SourceText = candidate2.SourceText,
								TargetLanguage = targetLanguage,
								Translation = candidate2.SourceText,
								AutomaticTranslation = candidate2.SourceText,
								FilePath = candidate2.FilePath
							});
							Progress.CachedSkipped++;
							Progress.Processed++;
							Progress.Translated++;
						}
						else if (UrtPackLibrary.Try(candidate2.SourceText, candidate2.Type, candidate2.DefType, candidate2.Key, candidate2.ModPackageId, targetLanguage, out packHit) && TranslationCache.IsGoodStored(candidate2.SourceText, packHit, targetLanguage))
						{
							cache.Put(new CachedTranslation
							{
								ModName = candidate2.ModName,
								ModPackageId = candidate2.ModPackageId,
								Type = candidate2.Type,
								DefType = candidate2.DefType,
								Key = candidate2.Key,
								SourceLanguage = candidate2.SourceLanguage,
								SourceText = candidate2.SourceText,
								TargetLanguage = targetLanguage,
								Translation = packHit,
								AutomaticTranslation = packHit,
								FilePath = candidate2.FilePath
							});
							Progress.CachedSkipped++;
							Progress.Processed++;
							Progress.Translated++;
						}
						else if (UrtOptEngine.TryRam(candidate2.SourceText, targetLanguage, out ram) && !string.IsNullOrWhiteSpace(ram) && !string.Equals(ram.Trim(), (candidate2.SourceText ?? "").Trim(), StringComparison.Ordinal) && TranslationCache.IsGoodStored(candidate2.SourceText, ram, targetLanguage))
						{
							cache.Put(new CachedTranslation
							{
								ModName = candidate2.ModName,
								ModPackageId = candidate2.ModPackageId,
								Type = candidate2.Type,
								DefType = candidate2.DefType,
								Key = candidate2.Key,
								SourceLanguage = candidate2.SourceLanguage,
								SourceText = candidate2.SourceText,
								TargetLanguage = targetLanguage,
								Translation = ram,
								AutomaticTranslation = ram,
								FilePath = candidate2.FilePath
							});
							Progress.CachedSkipped++;
							Progress.Processed++;
							Progress.Translated++;
						}
						else
						{
							pending.Add(candidate2);
							cached2 = null;
							packHit = null;
							ram = null;
						}
					}
				}
				new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				await Task.Run(delegate
				{
					UrtLivePrewarmPriority.Sort(pending);
				}, cancellationToken);
				foreach (TranslationCandidate item in pending)
				{
					item.SourceLanguage = EffectiveSourceLanguage(item, targetLanguage);
				}
				await Task.Run(delegate
				{
					UrtLivePrewarmPriority.SortBySourceLanguage(pending);
				}, cancellationToken);
				while (pending.Count > 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
					List<TranslationCandidate> chunk = new List<TranslationCandidate>();
					int chunkChars = 0;
					HashSet<string> seenText = new HashSet<string>(StringComparer.Ordinal);
					string chunkSourceLanguage = ((pending[0] == null) ? "" : (pending[0].SourceLanguage ?? ""));
					foreach (TranslationCandidate item3 in pending)
					{
						string itemSourceLanguage = ((item3 == null) ? "" : (item3.SourceLanguage ?? ""));
						if (!string.Equals(itemSourceLanguage, chunkSourceLanguage, StringComparison.OrdinalIgnoreCase))
						{
							break;
						}
						string src = item3.SourceText ?? "";
						if (!seenText.Add(src) && chunk.Count > 0)
						{
							chunk.Add(item3);
							continue;
						}
						if (chunk.Count >= 200 || (chunk.Count > 0 && chunkChars + src.Length > 49500))
						{
							break;
						}
						chunk.Add(item3);
						chunkChars += src.Length;
					}
					if (chunk.Count == 0)
					{
						chunk = pending.Take(Math.Min(120, pending.Count)).ToList();
					}
					foreach (TranslationCandidate item2 in chunk)
					{
						item2.SourceLanguage = chunkSourceLanguage;
						pending.Remove(item2);
					}
					Progress.CurrentBatchSize = chunk.Count;
					for (int hi = 0; hi < chunk.Count; hi++)
					{
						if (chunk[hi] != null)
						{
							RuntimeLiveQueue.Hold(chunk[hi].SourceText);
						}
					}
					List<TranslationCandidate> remaining = new List<TranslationCandidate>(chunk);
					Dictionary<TranslationCandidate, string> resolvedTranslations = new Dictionary<TranslationCandidate, string>();
					Dictionary<TranslationCandidate, string> resolvedProviders = new Dictionary<TranslationCandidate, string>();
					if (!forceIgnoreCache)
					{
						string tgt = LanguageCodeMapper.Canonical(targetLanguage);
						for (int ri = remaining.Count - 1; ri >= 0; ri--)
						{
							TranslationCandidate cand2 = remaining[ri];
							if (cand2 != null && cache.TryGet(cand2.ModPackageId, cand2.Type, cand2.DefType, cand2.Key, tgt, out var hit) && hit != null && !string.IsNullOrWhiteSpace(hit.Translation) && hit.Translation != cand2.SourceText)
							{
								remaining.RemoveAt(ri);
							}
							hit = null;
						}
					}
					if (!providerSettings.EnableAzure || string.IsNullOrWhiteSpace(providerSettings.AzureApiKey))
					{
						for (int mi = remaining.Count - 1; mi >= 0; mi--)
						{
							TranslationCandidate cand = remaining[mi];
							if (cand != null && UrtScriptSplit.IsMixed(cand.SourceText))
							{
								string stitched = await StitchMixedAsync(cand.SourceText, targetLanguage, providerSettings, cancellationToken);
								if (!string.IsNullOrWhiteSpace(stitched) && !(stitched == cand.SourceText) && IsUsableProviderTranslation(cand.SourceText, stitched))
								{
									resolvedTranslations[cand] = stitched;
									resolvedProviders[cand] = "script-split";
									remaining.RemoveAt(mi);
								}
							}
						}
					}
					List<ProviderAdapter> providers = BuildProviders(deepL, googleCloud, myMemory, simplyTranslate, yandex, providerSettings);
					if (providers.Count > 1)
					{
						try
						{
							await TranslateProviderPoolAsync(remaining, resolvedTranslations, resolvedProviders, providers, targetLanguage, providerSettings, cancellationToken);
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception ex3)
						{
							Exception ex = ex3;
							MLog.Warn("[URT] Parallel provider pool failed; falling back to serial chain: " + ex.GetType().Name + ": " + ex.Message);
						}
					}
					if (remaining.Count > 0)
					{
						await TranslateProviderChainSerialAsync(remaining, resolvedTranslations, resolvedProviders, providers, targetLanguage, providerSettings, cancellationToken);
					}
					foreach (KeyValuePair<TranslationCandidate, string> pair in resolvedTranslations)
					{
						TranslationCandidate candidate3 = pair.Key;
						string providerName = resolvedProviders[candidate3];
						CachedTranslation cached3 = new CachedTranslation
						{
							ModName = candidate3.ModName,
							ModPackageId = candidate3.ModPackageId,
							Type = candidate3.Type,
							DefType = candidate3.DefType,
							Key = candidate3.Key,
							SourceLanguage = candidate3.SourceLanguage,
							SourceText = candidate3.SourceText,
							TargetLanguage = targetLanguage,
							Translation = pair.Value,
							AutomaticTranslation = pair.Value,
							FilePath = candidate3.FilePath
						};
						if (!cache.Put(cached3))
						{
							if (!lastFailedCandidates.Contains(candidate3))
							{
								lastFailedCandidates.Add(candidate3);
							}
							Progress.Processed++;
							Progress.Failed++;
							continue;
						}
						UrtApplyQueue.Enqueue(cached3);
						Progress.Processed++;
						Progress.Translated++;
						if (!string.Equals(providerName, "DeepL", StringComparison.OrdinalIgnoreCase))
						{
							Progress.FallbackTranslated++;
						}
					}
					if (remaining.Count > 0)
					{
						foreach (TranslationCandidate failedCandidate in remaining)
						{
							if (!lastFailedCandidates.Contains(failedCandidate))
							{
								lastFailedCandidates.Add(failedCandidate);
							}
						}
						Progress.Processed += remaining.Count;
						Progress.Failed += remaining.Count;
						Progress.Status = "All enabled translation providers failed for " + remaining.Count + " entries.";
					}
					else
					{
						Progress.Status = "Provider chain completed: " + chunk.Count + " entries resolved.";
					}
					await Task.Yield();
					RuntimeLiveQueue.ReleaseAllHolds();
				}
				Progress.Finished = true;
				Progress.Status = ((Progress.Failed == 0) ? "Translation batch complete." : ("Translation batch complete with " + Progress.Failed + " failed entries."));
			}
			catch (OperationCanceledException)
			{
				Progress.Status = "Translation cancelled.";
				throw;
			}
			finally
			{
				Progress.Running = false;
				RuntimeLiveQueue.ReleaseAllHolds();
			}
		}

		private static bool IsUsefulCache(CachedTranslation cached, string source)
		{
			if (cached == null)
			{
				return false;
			}
			string text = cached.Translation;
			if (string.IsNullOrWhiteSpace(text))
			{
				text = cached.AutomaticTranslation;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = source ?? "";
			if (string.Equals(text.Trim(), text2.Trim(), StringComparison.Ordinal))
			{
				return false;
			}
			return TranslationCache.IsUsableTranslation(cached, source);
		}

		private static bool SkipForSpeed(TranslationCandidate c)
		{
			if (c == null)
			{
				return true;
			}
			string text = (c.SourceText ?? "").Trim();
			if (text.Length < 3)
			{
				return true;
			}
			if (Regex.IsMatch(text, "^[0-9.\\\\-]+$"))
			{
				return true;
			}
			return false;
		}

		private static async Task<string> StitchMixedAsync(string text, string targetLanguage, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			List<string> runs = UrtScriptSplit.Runs(text);
			if (runs.Count == 0)
			{
				return null;
			}
			GoogleWebTranslateProvider web = new GoogleWebTranslateProvider();
			StringBuilder sb = new StringBuilder();
			bool any = false;
			foreach (string run in runs)
			{
				if (!UrtScriptSplit.IsForeignRun(run))
				{
					sb.Append(run);
					continue;
				}
				TranslationProviderResult r;
				try
				{
					r = await web.TranslateBatchAsync(new List<string> { run }, "auto", targetLanguage, settings, cancellationToken);
				}
				catch
				{
					r = null;
				}
				string piece = ((r != null && r.Translations != null && r.Translations.Count > 0) ? r.Translations[0] : "");
				if (!string.IsNullOrWhiteSpace(piece) && piece != run)
				{
					sb.Append(piece);
					any = true;
				}
				else
				{
					sb.Append(run);
				}
			}
			return any ? sb.ToString() : null;
		}

		private static ProtectedTextBatch ProtectRimWorldTags(IList<string> texts)
		{
			ProtectedTextBatch protectedTextBatch = new ProtectedTextBatch();
			foreach (string item2 in texts ?? new List<string>())
			{
				string text = item2 ?? "";
				Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
				int index = 0;
				string text2 = UrtResultValidator.RulePrefixOf(text);
				if (text2 != null)
				{
					string text3 = "[[#" + index + "]]";
					index++;
					map[text3] = text2;
					text = text3 + " " + text.Substring(text2.Length).TrimStart(Array.Empty<char>());
				}
				string item = Regex.Replace(text, "<[^>\\r\\n]+>|\\$[A-Za-z_][A-Za-z0-9_.]*|\\[[A-Za-z_][^\\]\\r\\n]*\\]|\\{[0-9]+\\}|\\{[A-Za-z_][^{}\\r\\n]*\\}|\\\\n|\\(\\*[A-Za-z_][^)\\r\\n]*\\)|\\(/[A-Za-z_][A-Za-z0-9_]*\\)", delegate(Match match)
				{
					string text4 = "[[#" + index + "]]";
					index++;
					map[text4] = match.Value;
					return (match.Value == "\\n") ? (" " + text4 + " ") : text4;
				});
				protectedTextBatch.Texts.Add(item);
				protectedTextBatch.Maps.Add(map);
			}
			return protectedTextBatch;
		}

		private static bool WorthForcingToTarget(string src, string targetLanguage)
		{
			if (string.IsNullOrWhiteSpace(src))
			{
				return false;
			}
			string text = src.Trim();
			if (text.Length < 3 || text.Length > 180)
			{
				return false;
			}
			if (LanguageDetector.LooksLikeCzech(text))
			{
				return false;
			}
			bool flag = text.IndexOf(' ') >= 0;
			if (!flag && text.IndexOf('_') >= 0)
			{
				return false;
			}
			if (!flag)
			{
				for (int i = 1; i < text.Length; i++)
				{
					if (char.IsUpper(text[i]) && char.IsLetter(text[i - 1]))
					{
						return false;
					}
				}
			}
			int num = 0;
			for (int j = 0; j < text.Length; j++)
			{
				if (char.IsLetter(text[j]) && text[j] < '\u0080')
				{
					num++;
				}
			}
			return num >= 3;
		}

		private static string ExtractForcedLabel(string translated)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return "";
			}
			string text = translated.Trim();
			int num = text.IndexOf(':');
			if (num < 0 || num >= text.Length - 1)
			{
				return "";
			}
			text = text.Substring(num + 1).Trim().Trim('"', '\'', '«', '»', '.', ' ');
			return text.Trim();
		}

		private async Task ForceTargetEchoes(List<TranslationCandidate> echoes, Dictionary<string, List<TranslationCandidate>> clones, ProviderAdapter azure, string targetLanguage, TranslationProviderSettings providerSettings, TranslationCache cache, CancellationToken cancellationToken, List<TranslationCandidate> pending)
		{
			List<TranslationCandidate> unique = new List<TranslationCandidate>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < echoes.Count; i++)
			{
				TranslationCandidate c2 = echoes[i];
				if (c2 != null && !string.IsNullOrWhiteSpace(c2.SourceText) && seen.Add(c2.SourceText) && WorthForcingToTarget(c2.SourceText, targetLanguage))
				{
					unique.Add(c2);
				}
			}
			if (unique.Count == 0)
			{
				return;
			}
			MLog.Info("[URT] Force target-language " + targetLanguage + " on " + unique.Count + " names Azure only recapitalized.");
			int hit = 0;
			string sample = null;
			for (int j = 0; j < unique.Count; j += 30)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int m = Math.Min(30, unique.Count - j);
				List<string> texts = new List<string>(m);
				for (int k = 0; k < m; k++)
				{
					texts.Add("RimWorld label: " + unique[j + k].SourceText);
				}
				ProtectedTextBatch pb = ProtectRimWorldTags(texts);
				TranslationProviderResult response = await TranslateProviderIsolatedAsync(azure, pb.Texts, "", targetLanguage, providerSettings, cancellationToken);
				if (response == null || response.Translations == null)
				{
					continue;
				}
				for (int l = 0; l < m && l < response.Translations.Count && l < pb.Maps.Count; l++)
				{
					string restored = RestoreRimWorldTags(response.Translations[l], pb.Maps[l]);
					string extracted = LanguageDetector.RepairMarkup(StripBrokenPlaceholders(ExtractForcedLabel(restored)));
					string src = unique[j + l].SourceText ?? "";
					if (string.IsNullOrWhiteSpace(extracted) || LanguageDetector.HasBrokenMarkup(extracted) || !IsUsableProviderTranslation(src, extracted))
					{
						continue;
					}
					if (sample == null)
					{
						sample = "\"" + SanitizeDiag(src) + "\" -> \"" + SanitizeDiag(extracted) + "\"";
					}
					if (clones == null || !clones.TryGetValue(src, out var all) || all == null || all.Count == 0)
					{
						all = new List<TranslationCandidate> { unique[j + l] };
					}
					for (int ai = 0; ai < all.Count; ai++)
					{
						TranslationCandidate one = all[ai];
						if (one != null)
						{
							CachedTranslation cached = new CachedTranslation
							{
								ModName = one.ModName,
								ModPackageId = one.ModPackageId,
								Type = one.Type,
								DefType = one.DefType,
								Key = one.Key,
								SourceLanguage = one.SourceLanguage,
								SourceText = one.SourceText,
								TargetLanguage = targetLanguage,
								Translation = extracted,
								AutomaticTranslation = extracted,
								FilePath = one.FilePath
							};
							if (cache.Put(cached))
							{
								UrtApplyQueue.Enqueue(cached);
								Progress.Processed++;
								Progress.Translated++;
								hit++;
							}
						}
					}
					pending?.RemoveAll((TranslationCandidate c) => c != null && string.Equals(c.SourceText, src, StringComparison.Ordinal));
					all = null;
				}
			}
			MLog.Info("[URT] Force target-language hit=" + hit + " of " + unique.Count + ((sample == null) ? "" : (" " + sample)));
		}

		private async Task RecoverForeignRuns(List<TranslationCandidate> pending, Dictionary<string, List<TranslationCandidate>> clones, ProviderAdapter azure, string targetLanguage, TranslationProviderSettings providerSettings, TranslationCache cache, CancellationToken cancellationToken)
		{
			if (pending == null || pending.Count == 0 || azure == null || cache == null)
			{
				return;
			}
			List<string> runs = new List<string>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < pending.Count; i++)
			{
				TranslationCandidate c = pending[i];
				if (c == null)
				{
					continue;
				}
				List<string> parts = ForeignRuns(c.SourceText);
				for (int p = 0; p < parts.Count; p++)
				{
					if (seen.Add(parts[p]))
					{
						runs.Add(parts[p]);
					}
				}
			}
			if (runs.Count == 0)
			{
				return;
			}
			Dictionary<string, string> translated = new Dictionary<string, string>(StringComparer.Ordinal);
			for (int j = 0; j < runs.Count; j += 40)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int n = Math.Min(40, runs.Count - j);
				List<string> slice = runs.GetRange(j, n);
				TranslationProviderResult response = await TranslateProviderIsolatedAsync(azure, slice, "", targetLanguage, providerSettings, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (response == null || response.Translations == null)
				{
					continue;
				}
				for (int l = 0; l < n && l < response.Translations.Count; l++)
				{
					string src = slice[l];
					string dst = StripBrokenPlaceholders(response.Translations[l] ?? "");
					if (IsUsableProviderTranslation(src, dst) && !string.Equals(src, dst, StringComparison.Ordinal))
					{
						translated[src] = dst;
					}
				}
			}
			if (translated.Count == 0)
			{
				MLog.Info("[URT] Foreign runs 0/" + runs.Count + " translated.");
				return;
			}
			List<string> keys = new List<string>(translated.Keys);
			keys.Sort((string a, string b) => b.Length.CompareTo(a.Length));
			List<TranslationCandidate> still = new List<TranslationCandidate>();
			HashSet<TranslationCandidate> done = new HashSet<TranslationCandidate>();
			int hit = 0;
			for (int k = 0; k < pending.Count; k++)
			{
				TranslationCandidate cand = pending[k];
				if (cand == null || done.Contains(cand))
				{
					continue;
				}
				string src2 = cand.SourceText ?? "";
				string next = src2;
				for (int m = 0; m < keys.Count; m++)
				{
					if (next.IndexOf(keys[m], StringComparison.Ordinal) >= 0)
					{
						next = next.Replace(keys[m], translated[keys[m]]);
					}
				}
				if (string.Equals(next, src2, StringComparison.Ordinal) || !IsUsableProviderTranslation(src2, next))
				{
					still.Add(cand);
					continue;
				}
				if (clones == null || !clones.TryGetValue(src2, out var all) || all == null || all.Count == 0)
				{
					all = new List<TranslationCandidate> { cand };
				}
				for (int a2 = 0; a2 < all.Count; a2++)
				{
					TranslationCandidate one = all[a2];
					if (one != null && !done.Contains(one))
					{
						done.Add(one);
						cache.Put(new CachedTranslation
						{
							ModName = one.ModName,
							ModPackageId = one.ModPackageId,
							Type = one.Type,
							DefType = one.DefType,
							Key = one.Key,
							SourceLanguage = one.SourceLanguage,
							SourceText = one.SourceText,
							TargetLanguage = targetLanguage,
							Translation = next,
							AutomaticTranslation = next,
							FilePath = one.FilePath
						});
						UrtApplyQueue.Enqueue(new CachedTranslation
						{
							ModName = one.ModName,
							ModPackageId = one.ModPackageId,
							Type = one.Type,
							DefType = one.DefType,
							Key = one.Key,
							SourceLanguage = one.SourceLanguage,
							SourceText = one.SourceText,
							TargetLanguage = targetLanguage,
							Translation = next,
							AutomaticTranslation = next,
							FilePath = one.FilePath
						});
						Progress.Processed++;
						Progress.Translated++;
						hit++;
					}
				}
				all = null;
			}
			pending.Clear();
			pending.AddRange(still);
			MLog.Info("[URT] Foreign runs " + translated.Count + "/" + runs.Count + " hit=" + hit + " left=" + pending.Count);
		}

		private static List<string> ForeignRuns(string text)
		{
			List<string> list = new List<string>();
			if (string.IsNullOrEmpty(text))
			{
				return list;
			}
			StringBuilder stringBuilder = new StringBuilder();
			foreach (char c in text)
			{
				if (IsForeignLetter(c))
				{
					stringBuilder.Append(c);
				}
				else if (stringBuilder.Length > 0)
				{
					if (stringBuilder.Length >= 2)
					{
						list.Add(stringBuilder.ToString());
					}
					stringBuilder.Length = 0;
				}
			}
			if (stringBuilder.Length >= 2)
			{
				list.Add(stringBuilder.ToString());
			}
			return list;
		}

		private static bool IsForeignLetter(char c)
		{
			if (c >= '\u3040' && c <= 'ヿ')
			{
				return true;
			}
			if (c >= '㐀' && c <= '\u9fff')
			{
				return true;
			}
			if (c >= '가' && c <= '\ud7af')
			{
				return true;
			}
			if (c >= 'Ѐ' && c <= 'ӿ')
			{
				return true;
			}
			return false;
		}

		private static string StripBrokenPlaceholders(string text)
		{
			if (string.IsNullOrEmpty(text) || text.IndexOf("[[", StringComparison.Ordinal) < 0)
			{
				return text ?? "";
			}
			string input = Regex.Replace(text, "\\[\\[\\s*#\\s*\\d+\\s*\\]\\]", "");
			input = Regex.Replace(input, "\\[\\[\\s*#\\s*\\d+", "");
			input = Regex.Replace(input, "\\[#\\s*\\d+\\s*\\]+", "");
			return input.Trim();
		}

		private static string RestoreRimWorldTags(string text, Dictionary<string, string> map)
		{
			string text2 = text ?? "";
			if (map == null || map.Count == 0)
			{
				return text2;
			}
			foreach (KeyValuePair<string, string> item in map)
			{
				text2 = text2.Replace(item.Key, item.Value);
			}
			foreach (KeyValuePair<string, string> item2 in map)
			{
				if (item2.Value != null && item2.Value.EndsWith("->", StringComparison.Ordinal))
				{
					text2 = text2.Replace(item2.Value + " ", item2.Value);
				}
			}
			if (map.ContainsValue("\\n"))
			{
				text2 = Regex.Replace(text2, " *\\\\n *", "\\n");
			}
			text2 = Regex.Replace(text2, "\\[\\[#(\\d+)\\]\\]|\\[#(\\d+)\\]+|_?\\[\\[#(\\d+)|__+\\s*URT(?:_PLACEHOLDER|_ZASTUPN[YÝ]|[^\\d]{0,48})?_?\\s*(\\d+)\\s*__*", delegate(Match match)
			{
				string s = (match.Groups[1].Success ? match.Groups[1].Value : (match.Groups[2].Success ? match.Groups[2].Value : (match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value)));
				if (int.TryParse(s, out var result))
				{
					string key = "[[#" + result + "]]";
					if (map.TryGetValue(key, out var value))
					{
						return value;
					}
				}
				return match.Value;
			}, RegexOptions.IgnoreCase);
			text2 = CleanupPawnTokensForTarget(text2, null);
			// b114: a token that did not come back (or came back changed) -> drop the answer, the original text stays
			if (!AllMaskedTokensBack(text2, map))
			{
				Interlocked.Increment(ref brokenTokenAnswers);
				return "";
			}
			return text2;
		}

		private static int brokenTokenAnswers;

		private static bool AllMaskedTokensBack(string restored, Dictionary<string, string> map)
		{
			if (string.IsNullOrEmpty(restored))
			{
				return false;
			}
			if (restored.IndexOf("[[#", StringComparison.Ordinal) >= 0 || Regex.IsMatch(restored, "\\[#\\s*\\d+\\s*\\]"))
			{
				return false;
			}
			Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (KeyValuePair<string, string> item in map)
			{
				string text = (item.Value ?? "").Trim();
				if (text.Length != 0)
				{
					dictionary.TryGetValue(text, out var value);
					dictionary[text] = value + 1;
				}
			}
			foreach (KeyValuePair<string, int> item2 in dictionary)
			{
				int num = 0;
				int startIndex = 0;
				while (num < item2.Value)
				{
					int num2 = restored.IndexOf(item2.Key, startIndex, StringComparison.Ordinal);
					if (num2 < 0)
					{
						break;
					}
					num++;
					startIndex = num2 + item2.Key.Length;
				}
				if (num < item2.Value)
				{
					return false;
				}
			}
			return true;
		}

		private static string CleanupPawnTokensForTarget(string text, string targetLanguage)
		{
			return text ?? "";
		}

		private static bool HasAllProtectedTokensRestored(string translated, Dictionary<string, string> map)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			if (map == null || map.Count == 0)
			{
				return true;
			}
			foreach (KeyValuePair<string, string> item in map)
			{
				string text = item.Value ?? "";
				if (!IsCriticalRimWorldToken(text) || translated.Contains(text))
				{
					continue;
				}
				return false;
			}
			if (UrtResultValidator.LooksLikeLeakedMask(translated))
			{
				return false;
			}
			if (string.Equals(LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage()), "Czech", StringComparison.OrdinalIgnoreCase) && translated.IndexOf("zástupn", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
			return true;
		}

		private static bool IsCriticalRimWorldToken(string token)
		{
			if (string.IsNullOrEmpty(token))
			{
				return false;
			}
			char c = token[0];
			return c == '[' || c == '<' || c == '$' || c == '{';
		}

		private static bool TranslationResultsContainUntranslatedSource(IList<TranslationCandidate> candidates, IList<string> translations)
		{
			if (candidates == null || translations == null || candidates.Count != translations.Count)
			{
				return true;
			}
			for (int i = 0; i < candidates.Count; i++)
			{
				string text = NormalizeComparisonText((candidates[i] == null) ? "" : candidates[i].SourceText);
				string b = NormalizeComparisonText(translations[i]);
				if (!string.IsNullOrWhiteSpace(text) && string.Equals(text, b, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static string EffectiveSourceLanguage(TranslationCandidate candidate, string targetLanguage)
		{
			if (candidate == null)
			{
				return "";
			}
			if (LanguageDetector.IsMixedLanguage(candidate.SourceText, targetLanguage))
			{
				return "";
			}
			string text = DetectScriptLanguage(candidate.SourceText);
			if (!string.IsNullOrEmpty(text))
			{
				return text;
			}
			string text2 = LanguageDetector.DetectEmbeddedSourceLanguage(candidate.SourceText);
			if (!string.IsNullOrEmpty(text2) && !string.Equals(text2, "English", StringComparison.OrdinalIgnoreCase))
			{
				return LanguageCodeMapper.Canonical(text2);
			}
			return LanguageCodeMapper.Canonical(candidate.SourceLanguage);
		}

		private static string RunSkipProviderKey(string providerName, string sourceLanguage)
		{
			return providerName + "|" + LanguageCodeMapper.Canonical(sourceLanguage);
		}

		private static string DetectScriptLanguage(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}
			bool flag = false;
			bool flag2 = false;
			bool flag3 = false;
			bool flag4 = false;
			bool flag5 = false;
			bool flag6 = false;
			bool flag7 = false;
			foreach (char c in text)
			{
				int num = c;
				if ((num >= 44032 && num <= 55215) || (num >= 4352 && num <= 4607))
				{
					flag = true;
				}
				else if ((num >= 12352 && num <= 12447) || (num >= 12448 && num <= 12543) || (num >= 12784 && num <= 12799))
				{
					flag2 = true;
				}
				else if ((num >= 13312 && num <= 19903) || (num >= 19968 && num <= 40959))
				{
					flag3 = true;
				}
				else if ((num >= 1536 && num <= 1791) || (num >= 1872 && num <= 1919))
				{
					flag4 = true;
				}
				else if (num >= 1024 && num <= 1279)
				{
					flag5 = true;
				}
				else if (num >= 2304 && num <= 2431)
				{
					flag6 = true;
				}
				else if (num <= 591 && char.IsLetter(c))
				{
					flag7 = true;
				}
			}
			if (flag && !flag2 && !flag3 && !flag4 && !flag5 && !flag6)
			{
				return "Korean";
			}
			if (flag2 && !flag && !flag4 && !flag5 && !flag6)
			{
				return "Japanese";
			}
			if (flag3 && !flag && !flag2 && !flag4 && !flag5 && !flag6)
			{
				return "Chinese";
			}
			if (flag4 && !flag && !flag2 && !flag3 && !flag5 && !flag6)
			{
				return "Arabic";
			}
			if (flag5 && !flag && !flag2 && !flag3 && !flag4 && !flag6)
			{
				return "Russian";
			}
			if (flag6 && !flag && !flag2 && !flag3 && !flag4 && !flag5)
			{
				return "Hindi";
			}
			if ((flag || flag2 || flag3 || flag4 || flag5 || flag6) && flag7)
			{
				return "";
			}
			return "";
		}

		private static string NormalizeComparisonText(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "";
			}
			string text = value.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
			while (text.Contains("  "))
			{
				text = text.Replace("  ", " ");
			}
			return text;
		}

		private static List<string> SplitReadable(string text, int maxLen)
		{
			List<string> list = new List<string>();
			if (string.IsNullOrEmpty(text) || text.Length <= maxLen || maxLen < 200)
			{
				list.Add(text ?? "");
				return list;
			}
			int num2;
			for (int i = 0; i < text.Length; i += num2)
			{
				int num = text.Length - i;
				num2 = Math.Min(maxLen, num);
				if (num2 < num)
				{
					int num3 = i + num2;
					int num4 = text.LastIndexOf('\n', num3 - 1, num2);
					if (num4 < i + 80)
					{
						num4 = text.LastIndexOf(' ', num3 - 1, Math.Min(num2, 600));
					}
					if (num4 >= i + 80)
					{
						num2 = num4 - i + 1;
					}
				}
				list.Add(text.Substring(i, num2));
			}
			return list;
		}

		private static List<List<TranslationCandidate>> SliceProviderChunks(List<TranslationCandidate> pending)
		{
			List<List<TranslationCandidate>> list = new List<List<TranslationCandidate>>();
			List<TranslationCandidate> list2 = new List<TranslationCandidate>(pending);
			while (list2.Count > 0)
			{
				List<TranslationCandidate> list3 = new List<TranslationCandidate>();
				int num = 0;
				HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
				foreach (TranslationCandidate item in list2)
				{
					string text = item.SourceText ?? "";
					if (!hashSet.Add(text) && list3.Count > 0)
					{
						list3.Add(item);
						continue;
					}
					if (list3.Count >= 200 || (list3.Count > 0 && num + text.Length > 49500))
					{
						break;
					}
					list3.Add(item);
					num += text.Length;
				}
				if (list3.Count == 0)
				{
					list3 = list2.Take(1).ToList();
				}
				for (int i = 0; i < list3.Count; i++)
				{
					list2.Remove(list3[i]);
				}
				list.Add(list3);
			}
			return list;
		}

		private static void LogProviderFailure(string provider, TranslationProviderResult response)
		{
			int tickCount = Environment.TickCount;
			if (!providerFailLog.TryGetValue(provider ?? "?", out var value) || tickCount - value >= 30000)
			{
				providerFailLog[provider ?? "?"] = tickCount;
				string text = ((response == null) ? "no response" : (response.Error ?? "no usable translation"));
				int num = response?.StatusCode ?? 0;
				MLog.Warn("[URT] Provider " + provider + " failed: " + text + ((num != 0) ? (" (HTTP " + num + ")") : "") + (UrtOptEngine.IsUsable(provider) ? "" : ("; paused " + UrtOptEngine.SecondsUntilUsable(provider) + "s")));
			}
		}

		private static List<ProviderAdapter> BuildProviders(DeepLProvider deepL, GoogleTranslateProvider googleCloud, MyMemoryTranslateProvider myMemory, SimplyTranslateAiProvider simplyTranslate, YandexTranslateProvider yandex, TranslationProviderSettings settings, bool skipAzure = false)
		{
			List<ProviderAdapter> list = new List<ProviderAdapter>();
			if (!skipAzure && settings.EnableAzure && !string.IsNullOrWhiteSpace(settings.AzureApiKey))
			{
				list.Add(new ProviderAdapter("Azure Translator", new AzureTranslateProvider().TranslateBatchAsync));
			}
			if (settings.EnableMicrosoftEdge)
			{
				list.Add(new ProviderAdapter("Microsoft Edge", new MicrosoftEdgeTranslateProvider().TranslateBatchAsync));
			}
			if (settings.EnableDeepL && !string.IsNullOrWhiteSpace(settings.DeepLApiKey))
			{
				list.Add(new ProviderAdapter("DeepL", async (IList<string> texts, string source, string target, TranslationProviderSettings s, CancellationToken ct) => Convert(await deepL.TranslateBatchAsync(s.DeepLApiKey, texts, CodeForDeepL(source), CodeForDeepL(target), 3, ct))));
			}
			if (settings.EnableGoogleCloud && !string.IsNullOrWhiteSpace(settings.GoogleCloudApiKey))
			{
				list.Add(new ProviderAdapter("Google Cloud Translation", async (IList<string> texts, string source, string target, TranslationProviderSettings s, CancellationToken ct) => Convert(await googleCloud.TranslateBatchAsync(s.GoogleCloudApiKey, texts, source, target, 3, ct))));
			}
			if (settings.EnableYandex && yandex != null)
			{
				list.Add(new ProviderAdapter(yandex.Name, yandex.TranslateBatchAsync));
			}
			if (settings.EnableGoogleWeb)
			{
				list.Add(new ProviderAdapter("Google web", new GoogleWebTranslateProvider().TranslateBatchAsync));
			}
			if (settings.EnableSimplyTranslate && simplyTranslate != null)
			{
				list.Add(new ProviderAdapter(simplyTranslate.Name, simplyTranslate.TranslateBatchAsync));
			}
			if (settings.EnableMyMemory && myMemory != null)
			{
				list.Add(new ProviderAdapter(myMemory.Name, myMemory.TranslateBatchAsync));
			}
			if (settings.EnableLingva)
			{
				list.Add(new ProviderAdapter("Lingva", new LingvaTranslateProvider().TranslateBatchAsync));
			}
			if (settings.EnableGemini && !string.IsNullOrWhiteSpace(settings.GeminiApiKey))
			{
				list.Add(new ProviderAdapter("Gemini", new GeminiTranslateProvider().TranslateBatchAsync));
			}
			if (settings.EnableCustomEndpoint)
			{
				list.Add(new ProviderAdapter("Custom endpoint", new CustomEndpointTranslateProvider().TranslateBatchAsync));
			}
			return list;
		}

		private static TranslationProviderResult ExpandUniqueResult(TranslationProviderResult response, List<string> uniqueTexts, List<TranslationCandidate> providerInput)
		{
			if (response == null || uniqueTexts == null || providerInput == null)
			{
				return response;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
			if (response.Translations != null)
			{
				for (int i = 0; i < uniqueTexts.Count && i < response.Translations.Count; i++)
				{
					dictionary[uniqueTexts[i] ?? ""] = response.Translations[i] ?? "";
				}
			}
			List<string> list = new List<string>(providerInput.Count);
			for (int j = 0; j < providerInput.Count; j++)
			{
				if (!dictionary.TryGetValue(providerInput[j].SourceText ?? "", out var value))
				{
					value = "";
				}
				list.Add(value);
			}
			response.Translations = list;
			return response;
		}

		private static async Task<TranslationProviderResult> TranslateProviderIsolatedAsync(ProviderAdapter provider, IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			if (texts == null || texts.Count == 0)
			{
				return new TranslationProviderResult
				{
					Error = "Batch is empty."
				};
			}
			if (UrtOptEngine.CircuitOpen(provider.Name))
			{
				return new TranslationProviderResult
				{
					Error = (provider.Name ?? "provider") + " circuit open"
				};
			}
			if (UrtOptEngine.IsCoolingDown(provider.Name))
			{
				return new TranslationProviderResult
				{
					Error = (provider.Name ?? "provider") + " cooldown (" + UrtOptEngine.CooldownRemainingSeconds(provider.Name) + "s)",
					StatusCode = 0
				};
			}
			IList<string> sendTexts = texts.Select((string t) => UrtGlossary.Before(t)).ToList();
			UrtOptEngine.ProviderCaps caps = UrtOptEngine.Caps(provider.Name);
			int ms = caps.TimeoutMs;
			if (!caps.SupportsBatch && texts.Count > 1)
			{
				int rounds = (texts.Count + Math.Max(1, caps.MaxConcurrency) - 1) / Math.Max(1, caps.MaxConcurrency);
				ms = (int)Math.Min(180000L, (long)caps.TimeoutMs * (long)rounds);
			}
			TranslationProviderResult response;
			try
			{
				response = await UrtOptEngine.GateAsync(provider.Name, async delegate
				{
					using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					cts.CancelAfter(ms);
					return await provider.TranslateAsync(sendTexts, source, target, settings, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
				}).ConfigureAwait(continueOnCapturedContext: false);
				if (response != null && response.StatusCode == 429)
				{
					UrtOptEngine.CoolDown(provider.Name, 20);
				}
				if (response == null || !response.Success)
				{
					LogProviderFailure(provider.Name, response);
				}
				if ((response == null || !response.Success) && (response == null || (response.StatusCode != 429 && response.StatusCode != 401 && response.StatusCode != 403)))
				{
					for (int i = 0; i < texts.Count; i++)
					{
						UrtOptEngine.RememberNegative(UrtOptEngine.Fingerprint(texts[i], target), provider.Name, 30);
					}
				}
			}
			catch (UrtOptEngine.ProviderCooldownException ex)
			{
				LogProviderFailure(provider.Name, new TranslationProviderResult
				{
					Error = ex.Message,
					StatusCode = 429
				});
				return new TranslationProviderResult
				{
					Error = ex.Message,
					StatusCode = 429
				};
			}
			catch (OperationCanceledException)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				for (int j = 0; j < texts.Count; j++)
				{
					UrtOptEngine.RememberNegative(UrtOptEngine.Fingerprint(texts[j], target), provider.Name, 45);
				}
				return new TranslationProviderResult
				{
					Error = (provider.Name ?? "provider") + " timeout " + ms / 1000 + "s"
				};
			}
			catch (Exception ex2)
			{
				response = new TranslationProviderResult
				{
					Error = ex2.GetType().Name + ": " + ex2.Message
				};
			}
			bool responseCountMismatch = response != null && response.Translations != null && response.Translations.Count != texts.Count;
			if (texts.Count > 1 && response != null && (response.StatusCode == 413 || response.StatusCode == 400 || response.StatusCode == 422 || responseCountMismatch))
			{
				int mid = texts.Count / 2;
				List<string> leftTexts = texts.Take(mid).ToList();
				List<string> rightTexts = texts.Skip(mid).ToList();
				return CombineProviderResponses(await TranslateProviderIsolatedAsync(provider, leftTexts, source, target, settings, cancellationToken), await TranslateProviderIsolatedAsync(provider, rightTexts, source, target, settings, cancellationToken), texts.Count);
			}
			if (response != null && response.Translations != null && response.Translations.Count != texts.Count)
			{
				while (response.Translations.Count < texts.Count)
				{
					response.Translations.Add("");
				}
				if (response.Translations.Count > texts.Count)
				{
					response.Translations = response.Translations.Take(texts.Count).ToList();
				}
				response.Success = response.Translations.All((string x) => !string.IsNullOrWhiteSpace(x));
			}
			if (texts.Count == 1 && (response == null || response.Translations == null || response.Translations.Count == 0))
			{
				return new TranslationProviderResult
				{
					Error = ((response == null) ? "Provider returned no response." : response.Error),
					Translations = new List<string> { "" },
					StatusCode = (response?.StatusCode ?? 0),
					Attempts = (response?.Attempts ?? 0),
					CharacterCount = (response?.CharacterCount ?? 0)
				};
			}
			return response ?? new TranslationProviderResult
			{
				Error = "Provider returned no response.",
				Translations = Enumerable.Repeat("", texts.Count).ToList()
			};
		}

		private static bool IsUnusableBatchResponse(TranslationProviderResult response, int expectedCount)
		{
			if (response == null || response.Translations == null)
			{
				return true;
			}
			if (response.Translations.Count != expectedCount)
			{
				return true;
			}
			for (int i = 0; i < response.Translations.Count; i++)
			{
				if (!string.IsNullOrWhiteSpace(response.Translations[i]))
				{
					return false;
				}
			}
			return true;
		}

		private static TranslationProviderResult CombineProviderResponses(TranslationProviderResult left, TranslationProviderResult right, int expectedCount)
		{
			TranslationProviderResult translationProviderResult = new TranslationProviderResult();
			translationProviderResult.Translations = new List<string>();
			if (left != null && left.Translations != null)
			{
				translationProviderResult.Translations.AddRange(left.Translations);
			}
			if (right != null && right.Translations != null)
			{
				translationProviderResult.Translations.AddRange(right.Translations);
			}
			while (translationProviderResult.Translations.Count < expectedCount)
			{
				translationProviderResult.Translations.Add("");
			}
			if (translationProviderResult.Translations.Count > expectedCount)
			{
				translationProviderResult.Translations = translationProviderResult.Translations.Take(expectedCount).ToList();
			}
			translationProviderResult.Success = translationProviderResult.Translations.All((string x) => !string.IsNullOrWhiteSpace(x));
			translationProviderResult.Error = (translationProviderResult.Success ? "" : "One or more isolated provider items failed.");
			translationProviderResult.StatusCode = ((right != null && right.StatusCode != 0) ? right.StatusCode : (left?.StatusCode ?? 0));
			translationProviderResult.Attempts = (left?.Attempts ?? 0) + (right?.Attempts ?? 0);
			translationProviderResult.CharacterCount = (left?.CharacterCount ?? 0) + (right?.CharacterCount ?? 0);
			return translationProviderResult;
		}

		private static TranslationProviderResult MergeProviderResponses(TranslationProviderResult primary, TranslationProviderResult retry, int expectedCount)
		{
			TranslationProviderResult translationProviderResult = new TranslationProviderResult();
			translationProviderResult.Translations = new List<string>();
			for (int i = 0; i < expectedCount; i++)
			{
				string text = ((primary != null && primary.Translations != null && i < primary.Translations.Count) ? primary.Translations[i] : "");
				string text2 = ((retry != null && retry.Translations != null && i < retry.Translations.Count) ? retry.Translations[i] : "");
				translationProviderResult.Translations.Add((!string.IsNullOrWhiteSpace(text)) ? text : text2);
			}
			translationProviderResult.Success = translationProviderResult.Translations.All((string x) => !string.IsNullOrWhiteSpace(x));
			translationProviderResult.Error = (translationProviderResult.Success ? "" : ((retry != null) ? retry.Error : ((primary == null) ? "Provider failed." : primary.Error)));
			translationProviderResult.StatusCode = ((retry != null && retry.StatusCode != 0) ? retry.StatusCode : (primary?.StatusCode ?? 0));
			translationProviderResult.Attempts = (primary?.Attempts ?? 0) + (retry?.Attempts ?? 0);
			translationProviderResult.CharacterCount = (primary?.CharacterCount ?? 0) + (retry?.CharacterCount ?? 0);
			return translationProviderResult;
		}

		private async Task TranslateProviderPoolAsync(List<TranslationCandidate> remaining, Dictionary<TranslationCandidate, string> resolvedTranslations, Dictionary<TranslationCandidate, string> resolvedProviders, List<ProviderAdapter> providers, string targetLanguage, TranslationProviderSettings providerSettings, CancellationToken cancellationToken)
		{
			if (remaining == null || remaining.Count == 0 || providers == null || providers.Count == 0)
			{
				return;
			}
			Dictionary<string, List<TranslationCandidate>> byText = new Dictionary<string, List<TranslationCandidate>>(StringComparer.Ordinal);
			List<string> uniqueOrder = new List<string>();
			for (int i = 0; i < remaining.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				TranslationCandidate c2 = remaining[i];
				if (c2 != null)
				{
					string text = c2.SourceText ?? "";
					if (!byText.TryGetValue(text, out var list))
					{
						list = (byText[text] = new List<TranslationCandidate>());
						uniqueOrder.Add(text);
					}
					list.Add(c2);
					list = null;
				}
			}
			if (uniqueOrder.Count == 0)
			{
				return;
			}
			List<ProviderAdapter> eligible = new List<ProviderAdapter>();
			for (int k = 0; k < providers.Count; k++)
			{
				ProviderAdapter provider = providers[k];
				if (provider != null && IsFastBatchProvider(provider.Name))
				{
					string runSkipKey = RunSkipProviderKey(sourceLanguage: FirstSourceLanguage(remaining), providerName: provider.Name);
					if (!RunSkipProviders.Contains(runSkipKey) && !UrtOptEngine.CircuitOpen(provider.Name) && !UrtOptEngine.IsCoolingDown(provider.Name))
					{
						eligible.Add(provider);
					}
				}
			}
			if (eligible.Count == 0)
			{
				return;
			}
			List<ParallelProviderBatchResult> batches = new List<ParallelProviderBatchResult>();
			int providerCursor = 0;
			for (int m = 0; m < uniqueOrder.Count; m++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string text2 = uniqueOrder[m];
				ProviderAdapter assigned = null;
				for (int tries = 0; tries < eligible.Count; tries++)
				{
					ProviderAdapter candidateProvider = eligible[providerCursor % eligible.Count];
					providerCursor++;
					string fingerprint = UrtOptEngine.Fingerprint(text2, targetLanguage);
					if (!UrtOptEngine.NegativeHit(fingerprint, candidateProvider.Name))
					{
						assigned = candidateProvider;
						break;
					}
				}
				if (assigned == null)
				{
					continue;
				}
				ParallelProviderBatchResult batch2 = null;
				int maxItems = Math.Max(1, UrtOptEngine.Caps(assigned.Name).MaxBatchItems);
				for (int b = 0; b < batches.Count; b++)
				{
					if (batches[b].Provider == assigned && batches[b].UniqueTexts.Count < maxItems)
					{
						batch2 = batches[b];
						break;
					}
				}
				if (batch2 == null)
				{
					batch2 = new ParallelProviderBatchResult
					{
						Provider = assigned
					};
					batches.Add(batch2);
				}
				batch2.UniqueTexts.Add(text2);
				if (byText.TryGetValue(text2, out var matching2) && matching2 != null && matching2.Count > 0)
				{
					batch2.Candidates.Add(matching2[0]);
				}
				matching2 = null;
			}
			if (batches.Count == 0)
			{
				return;
			}
			StringBuilder allocation = new StringBuilder();
			for (int i2 = 0; i2 < batches.Count; i2++)
			{
				if (i2 > 0)
				{
					allocation.Append(", ");
				}
				allocation.Append(batches[i2].Provider.Name).Append("=").Append(batches[i2].UniqueTexts.Count);
			}
			MLog.Info("[URT] Parallel provider pool: " + batches.Count + " workers; " + allocation.ToString() + " target=" + LanguageCodeMapper.Canonical(targetLanguage));
			Task<ParallelProviderBatchResult>[] tasks = new Task<ParallelProviderBatchResult>[batches.Count];
			for (int n = 0; n < batches.Count; n++)
			{
				ParallelProviderBatchResult batch3 = batches[n];
				Progress.CurrentProvider = "Parallel providers";
				Progress.Status = "Parallel providers: " + batches.Count + " workers, " + uniqueOrder.Count + " unique texts in flight…";
				tasks[n] = TranslateParallelProviderBatchAsync(batch3, FirstSourceLanguage(batch3.Candidates), targetLanguage, providerSettings, cancellationToken);
			}
			ParallelProviderBatchResult[] finished = await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
			int parallelInputChars = 0;
			for (int l = 0; l < batches.Count; l++)
			{
				parallelInputChars += batches[l].UniqueTexts.Sum((string x) => x?.Length ?? 0);
			}
			Progress.CharactersSent += parallelInputChars;
			for (int j = 0; j < finished.Length; j++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				ParallelProviderBatchResult batch = finished[j];
				if (batch == null || batch.Provider == null)
				{
					continue;
				}
				List<TranslationCandidate> inputCandidates = batch.Candidates;
				TranslationProviderResult response = batch.Response;
				RecordProviderDiagnostics(batch.Provider.Name, response, batch.UniqueTexts.Count, inputCandidates, targetLanguage);
				int resolvedThisProvider = 0;
				if (response != null && response.Translations != null)
				{
					int count = Math.Min(batch.UniqueTexts.Count, response.Translations.Count);
					for (int j2 = 0; j2 < count; j2++)
					{
						string sourceText = batch.UniqueTexts[j2] ?? "";
						string translated = StripBrokenPlaceholders(UrtGlossary.After(response.Translations[j2]));
						NoteEcho(batch.Provider.Name, sourceText, translated);
						if (!IsUsableProviderAnswer(batch.Provider.Name, sourceText, translated) || !byText.TryGetValue(sourceText, out var matching) || matching == null)
						{
							continue;
						}
						for (int k2 = 0; k2 < matching.Count; k2++)
						{
							TranslationCandidate candidate = matching[k2];
							if (candidate != null && !resolvedTranslations.ContainsKey(candidate))
							{
								resolvedTranslations[candidate] = translated;
								resolvedProviders[candidate] = batch.Provider.Name;
								resolvedThisProvider++;
							}
						}
						matching = null;
					}
				}
				if (response != null)
				{
					Progress.CharactersSent += response.CharacterCount;
				}
				int old;
				lock (Progress.StatsLock)
				{
					Progress.ProviderTranslated.TryGetValue(batch.Provider.Name, out old);
				}
				if (resolvedThisProvider > 0)
				{
					lock (Progress.StatsLock)
					{
						Progress.ProviderTranslated[batch.Provider.Name] = old + resolvedThisProvider;
					}
				}
				else if (IsHardProviderFailure(response))
				{
					lock (Progress.StatsLock)
					{
						Progress.ProviderFailed.TryGetValue(batch.Provider.Name, out old);
						Progress.ProviderFailed[batch.Provider.Name] = old + batch.UniqueTexts.Count;
					}
					RunSkipProviders.Add(RunSkipProviderKey(batch.Provider.Name, FirstSourceLanguage(inputCandidates)));
				}
			}
			if (resolvedTranslations.Count > 0)
			{
				remaining.RemoveAll((TranslationCandidate c) => c != null && resolvedTranslations.ContainsKey(c));
			}
		}

		private async Task<ParallelProviderBatchResult> TranslateParallelProviderBatchAsync(ParallelProviderBatchResult batch, string sourceLanguage, string targetLanguage, TranslationProviderSettings providerSettings, CancellationToken cancellationToken)
		{
			if (batch == null || batch.Provider == null || batch.UniqueTexts == null || batch.UniqueTexts.Count == 0)
			{
				return batch;
			}
			ProtectedTextBatch protectedBatch = ProtectRimWorldTags(batch.UniqueTexts);
			batch.Response = await TranslateProviderIsolatedAsync(batch.Provider, protectedBatch.Texts, sourceLanguage, targetLanguage, providerSettings, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (batch.Response != null && batch.Response.Translations != null)
			{
				for (int i = 0; i < batch.Response.Translations.Count && i < protectedBatch.Maps.Count; i++)
				{
					batch.Response.Translations[i] = RestoreRimWorldTags(batch.Response.Translations[i], protectedBatch.Maps[i]);
				}
			}
			return batch;
		}

		private async Task TranslateProviderChainSerialAsync(List<TranslationCandidate> remaining, Dictionary<TranslationCandidate, string> resolvedTranslations, Dictionary<TranslationCandidate, string> resolvedProviders, List<ProviderAdapter> providers, string targetLanguage, TranslationProviderSettings providerSettings, CancellationToken cancellationToken)
		{
			for (int providerIndex = 0; providerIndex < providers.Count; providerIndex++)
			{
				if (remaining.Count <= 0)
				{
					break;
				}
				cancellationToken.ThrowIfCancellationRequested();
				ProviderAdapter provider = providers[providerIndex];
				string sourceLanguage = ((remaining[0] == null) ? "" : (remaining[0].SourceLanguage ?? ""));
				string runSkipKey = RunSkipProviderKey(provider.Name, sourceLanguage);
				if (RunSkipProviders.Contains(runSkipKey) || UrtOptEngine.CircuitOpen(provider.Name))
				{
					Progress.Status = provider.Name + " skipped for this run.";
					continue;
				}
				List<TranslationCandidate> providerInput = new List<TranslationCandidate>();
				List<string> uniqueTexts = new List<string>();
				HashSet<string> seenSrc = new HashSet<string>(StringComparer.Ordinal);
				for (int ri = 0; ri < remaining.Count; ri++)
				{
					TranslationCandidate cand = remaining[ri];
					if (cand == null)
					{
						continue;
					}
					string fpItem = UrtOptEngine.Fingerprint(cand.SourceText, targetLanguage);
					if (!UrtOptEngine.NegativeHit(fpItem, provider.Name) && (IsFastBatchProvider(provider.Name) || !WasEchoed(cand.SourceText)))
					{
						providerInput.Add(cand);
						if (seenSrc.Add(cand.SourceText ?? ""))
						{
							uniqueTexts.Add(cand.SourceText ?? "");
						}
					}
				}
				if (providerInput.Count == 0)
				{
					continue;
				}
				Progress.CurrentProvider = provider.Name;
				int sendChars = 0;
				for (int u = 0; u < uniqueTexts.Count; u++)
				{
					sendChars += ((uniqueTexts[u] != null) ? uniqueTexts[u].Length : 0);
				}
				Progress.CharactersSent += sendChars;
				Progress.Status = "Trying " + provider.Name + " (" + (providerIndex + 1) + "/" + providers.Count + ") unique " + uniqueTexts.Count + "/" + providerInput.Count + " chars~" + sendChars;
				if (!loggedB71)
				{
					loggedB71 = true;
					MLog.Info("[URT] b73 fast pool is Azure and Edge only. Slow providers wait for leftovers.");
				}
				TranslationProviderResult response;
				try
				{
					ProtectedTextBatch protectedBatch = ProtectRimWorldTags(uniqueTexts);
					TranslationProviderResult uniqueResponse = await TranslateProviderIsolatedAsync(provider, protectedBatch.Texts, sourceLanguage, targetLanguage, providerSettings, cancellationToken);
					// b114: restore placeholders per UNIQUE text before expanding to all candidates (maps are indexed by unique text)
					if (uniqueResponse != null && uniqueResponse.Translations != null)
					{
						for (int k = 0; k < uniqueResponse.Translations.Count && k < protectedBatch.Maps.Count; k++)
						{
							uniqueResponse.Translations[k] = RestoreRimWorldTags(uniqueResponse.Translations[k], protectedBatch.Maps[k]);
						}
					}
					response = ExpandUniqueResult(uniqueResponse, uniqueTexts, providerInput);
					if (SupportsAutoDetect(provider.Name) && !IsHardProviderFailure(response))
					{
						List<int> unresolvedForAuto = new List<int>();
						for (int n = 0; n < providerInput.Count; n++)
						{
							if (!IsUsableProviderAnswer(translated: (response != null && response.Translations != null && n < response.Translations.Count) ? response.Translations[n] : "", sourceText: providerInput[n].SourceText ?? "", providerName: provider.Name))
							{
								unresolvedForAuto.Add(n);
							}
						}
						if (unresolvedForAuto.Count > 0)
						{
							List<TranslationCandidate> autoCandidates = unresolvedForAuto.Select((int i) => providerInput[i]).ToList();
							ProtectedTextBatch autoProtected = ProtectRimWorldTags(autoCandidates.Select((TranslationCandidate x) => x.SourceText).ToList());
							TranslationProviderResult autoDetected = await TranslateProviderIsolatedAsync(provider, autoProtected.Texts, "", targetLanguage, providerSettings, cancellationToken);
							if (autoDetected != null && autoDetected.Translations != null)
							{
								for (int m = 0; m < autoDetected.Translations.Count && m < autoProtected.Maps.Count; m++)
								{
									autoDetected.Translations[m] = RestoreRimWorldTags(autoDetected.Translations[m], autoProtected.Maps[m]);
								}
								if (response == null)
								{
									response = new TranslationProviderResult
									{
										Translations = Enumerable.Repeat("", providerInput.Count).ToList(),
										Error = "Initial provider response was empty."
									};
								}
								if (response.Translations == null)
								{
									response.Translations = Enumerable.Repeat("", providerInput.Count).ToList();
								}
								while (response.Translations.Count < providerInput.Count)
								{
									response.Translations.Add("");
								}
								if (response.Translations.Count > providerInput.Count)
								{
									response.Translations = response.Translations.Take(providerInput.Count).ToList();
								}
								for (int l = 0; l < unresolvedForAuto.Count && l < autoDetected.Translations.Count; l++)
								{
									int originalIndex = unresolvedForAuto[l];
									string autoText = autoDetected.Translations[l];
									if (IsUsableProviderTranslation(providerInput[originalIndex].SourceText, autoText))
									{
										response.Translations[originalIndex] = autoText;
									}
								}
							}
						}
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					response = new TranslationProviderResult
					{
						Error = ex.GetType().Name + ": " + ex.Message
					};
				}
				RecordProviderDiagnostics(provider.Name, response, providerInput.Count, providerInput, targetLanguage);
				int resolvedThisProvider = 0;
				if (response != null && response.Translations != null)
				{
					int count = Math.Min(providerInput.Count, response.Translations.Count);
					for (int j = 0; j < count; j++)
					{
						string translated = StripBrokenPlaceholders(UrtGlossary.After(response.Translations[j]));
						string sourceText = providerInput[j].SourceText ?? "";
						NoteEcho(provider.Name, sourceText, translated);
						if (IsUsableProviderAnswer(provider.Name, sourceText, translated))
						{
							TranslationCandidate candidate = providerInput[j];
							if (!resolvedTranslations.ContainsKey(candidate))
							{
								resolvedTranslations[candidate] = translated;
								resolvedProviders[candidate] = provider.Name;
								resolvedThisProvider++;
							}
						}
					}
				}
				if (response != null)
				{
					Progress.CharactersSent += response.CharacterCount;
				}
				if (resolvedThisProvider > 0)
				{
					HashSet<TranslationCandidate> resolvedSet = new HashSet<TranslationCandidate>(resolvedTranslations.Keys);
					remaining.RemoveAll((TranslationCandidate x) => resolvedSet.Contains(x));
					lock (Progress.StatsLock)
					{
						Progress.ProviderTranslated.TryGetValue(provider.Name, out var old);
						Progress.ProviderTranslated[provider.Name] = old + resolvedThisProvider;
					}
					Progress.Status = provider.Name + " translated " + resolvedThisProvider + "; " + remaining.Count + " still need a fallback.";
				}
				else if (IsHardProviderFailure(response))
				{
					RunSkipProviders.Add(RunSkipProviderKey(provider.Name, sourceLanguage));
					Progress.Status = provider.Name + " failed once, skipped for the rest of this run.";
				}
				else
				{
					Progress.Status = provider.Name + " returned nothing usable. Next provider.";
				}
				await Task.Yield();
			}
		}

		private static bool IsFastBatchProvider(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			return name.IndexOf("Azure", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Microsoft Edge", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("DeepL", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Google Cloud", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static string FirstSourceLanguage(IList<TranslationCandidate> candidates)
		{
			if (candidates == null)
			{
				return "";
			}
			for (int i = 0; i < candidates.Count; i++)
			{
				if (candidates[i] != null && !string.IsNullOrWhiteSpace(candidates[i].SourceLanguage))
				{
					return candidates[i].SourceLanguage;
				}
			}
			return "";
		}

		private void RecordProviderDiagnostics(string providerName, TranslationProviderResult response, int attemptedCount, IList<TranslationCandidate> inputs, string targetLanguage)
		{
			if (Progress == null || string.IsNullOrWhiteSpace(providerName))
			{
				return;
			}
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			if (response != null && response.Translations != null)
			{
				for (int i = 0; i < attemptedCount; i++)
				{
					string text = ((i < response.Translations.Count) ? response.Translations[i] : "");
					string text2 = ((inputs != null && i < inputs.Count && inputs[i] != null) ? inputs[i].SourceText : "");
					if (IsUsableProviderTranslation(text2, text))
					{
						num++;
					}
					else if (string.IsNullOrWhiteSpace(text))
					{
						num3++;
					}
					else if (string.Equals(NormalizeComparisonText(text2), NormalizeComparisonText(text), StringComparison.OrdinalIgnoreCase))
					{
						num2++;
					}
					else
					{
						num4++;
					}
				}
			}
			else
			{
				num3 = attemptedCount;
			}
			int num5 = Math.Max(0, attemptedCount - num);
			string text3 = BuildProviderFailureDetail(response);
			if (num2 > 0)
			{
				text3 = text3 + " | unchanged=" + num2;
			}
			if (num3 > 0)
			{
				text3 = text3 + " | empty=" + num3;
			}
			if (num4 > 0)
			{
				text3 = text3 + " | rejected=" + num4;
			}
			if (num5 > 0)
			{
				lock (Progress.StatsLock)
				{
					Progress.ProviderFailed.TryGetValue(providerName, out var value);
					Progress.ProviderFailed[providerName] = value + num5;
					if (!string.IsNullOrWhiteSpace(text3))
					{
						Progress.ProviderLastError[providerName] = text3;
					}
				}
			}
			else if (!string.IsNullOrWhiteSpace(text3))
			{
				lock (Progress.StatsLock)
				{
					Progress.ProviderLastError[providerName] = text3;
				}
			}
			try
			{
				string configFolderPath = GenFilePaths.ConfigFolderPath;
				string text4 = Path.Combine(configFolderPath, "UniversalRimTranslator", "Cache");
				Directory.CreateDirectory(text4);
				string path = Path.Combine(text4, "UniversalRimTranslator_Diagnostics.txt");
				using StreamWriter streamWriter = new StreamWriter(path, append: true);
				streamWriter.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | Provider=" + providerName + " | Attempted=" + attemptedCount + " | Usable=" + num + " | Unchanged=" + num2 + " | Empty=" + num3 + " | Target=" + targetLanguage + " | HTTP=" + (response?.StatusCode ?? 0) + " | Error=" + ((response == null) ? "No response." : (response.Error ?? "")));
				if (attemptedCount <= 8 && response != null && response.RawResponses != null && response.RawResponses.Count > 0)
				{
					for (int j = 0; j < response.RawResponses.Count; j++)
					{
						streamWriter.WriteLine("  RAW[" + j + "]=" + SanitizeDiag(response.RawResponses[j]));
					}
				}
			}
			catch
			{
			}
		}

		private static string ExplainValidationFailure(string sourceText, string translated)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return "EMPTY";
			}
			if (string.Equals(NormalizeComparisonText(sourceText), NormalizeComparisonText(translated), StringComparison.OrdinalIgnoreCase))
			{
				return "UNCHANGED";
			}
			return "REJECTED";
		}

		private static string SanitizeDiag(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			string text2 = text.Replace("\r", " ").Replace("\n", " ");
			if (text2.Length > 400)
			{
				return text2.Substring(0, 400) + "...";
			}
			return text2;
		}

		private static string BuildProviderFailureDetail(TranslationProviderResult response)
		{
			if (response == null)
			{
				return "No response.";
			}
			string text = response.Error ?? "";
			if (response.StatusCode != 0)
			{
				if (string.IsNullOrWhiteSpace(text))
				{
					return "HTTP " + response.StatusCode;
				}
				return "HTTP " + response.StatusCode + ": " + text;
			}
			return string.IsNullOrWhiteSpace(text) ? "Empty/invalid response." : text;
		}

		private static bool IsHardProviderFailure(TranslationProviderResult response)
		{
			if (response == null)
			{
				return true;
			}
			int statusCode = response.StatusCode;
			return statusCode == 401 || statusCode == 403 || statusCode == 404 || statusCode == 408 || statusCode == 413 || statusCode == 429 || statusCode >= 500;
		}

		private static bool SupportsAutoDetect(string providerName)
		{
			return string.Equals(providerName, "Google Cloud Translation", StringComparison.OrdinalIgnoreCase) || string.Equals(providerName, "SimplyTranslate", StringComparison.OrdinalIgnoreCase);
		}

		public static bool IsUsableProviderTranslation(string sourceText, string translated)
		{
			return UrtResultValidator.Judge(sourceText, translated, UniversalRimTranslatorSettings.PlayerTargetLanguage()) == null;
		}

		private static bool IsUsableProviderAnswer(string providerName, string sourceText, string translated)
		{
			string text = UrtResultValidator.Judge(sourceText, translated, UniversalRimTranslatorSettings.PlayerTargetLanguage());
			if (text == null)
			{
				return true;
			}
			if (text != "empty" && text != "unchanged" && judgeSamples < 40)
			{
				bool flag;
				lock (SampledSources)
				{
					flag = SampledSources.Add(sourceText ?? "");
				}
				if (flag && Interlocked.Increment(ref judgeSamples) <= 40)
				{
					MLog.Info("[URT] scan rejected " + text + " [" + providerName + "]: \"" + ShortForLog(sourceText) + "\" -> \"" + ShortForLog(translated) + "\"");
				}
			}
			return false;
		}

		private static string ShortForLog(string text)
		{
			text = (text ?? "").Replace("\r", "").Replace("\n", "\\n");
			return (text.Length > 110) ? (text.Substring(0, 110) + "…") : text;
		}

		private static bool IsUsableProviderTranslationOld(string sourceText, string translated)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			if (translated.IndexOf("[[#", StringComparison.Ordinal) >= 0)
			{
				return false;
			}
			if (HasStrongCjk(translated) || HasStrongJapanese(translated) || HasStrongHangul(translated))
			{
				return false;
			}
			if (!UrtResultValidator.RulePrefixKept(sourceText, translated))
			{
				return false;
			}
			string text = NormalizeComparisonText(sourceText);
			string text2 = NormalizeComparisonText(translated);
			if (string.IsNullOrWhiteSpace(text2))
			{
				return false;
			}
			if (!string.IsNullOrWhiteSpace(text) && string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
			{
				return LanguageDetector.LooksLikeProperNameOrCode(sourceText) && text.Length <= 24;
			}
			string targetLanguage = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			if (LanguageDetector.IsHalfTranslated(translated, targetLanguage) && !LanguageDetector.LooksLikeCzech(translated))
			{
				return false;
			}
			return LanguageDetector.IsBetterTranslation(sourceText, translated, UniversalRimTranslatorSettings.PlayerTargetLanguage());
		}

		private static bool HasStrongHangul(string text)
		{
			int num = 0;
			int num2 = 0;
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				if (char.IsLetter(c))
				{
					num++;
					if (c >= '가' && c <= '\ud7af')
					{
						num2++;
					}
				}
			}
			return num >= 3 && num2 * 2 >= num;
		}

		private static bool HasStrongJapanese(string text)
		{
			int num = 0;
			int num2 = 0;
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				if (char.IsLetter(c))
				{
					num++;
					if ((c >= '\u3040' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ'))
					{
						num2++;
					}
				}
			}
			return num >= 3 && num2 * 2 >= num;
		}

		private static bool HasStrongCjk(string text)
		{
			int num = 0;
			int num2 = 0;
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				if (char.IsLetter(c))
				{
					num++;
					if ((c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '\u9fff'))
					{
						num2++;
					}
				}
			}
			return num >= 3 && num2 * 2 >= num;
		}

		private static bool HasStrongCyrillic(string text)
		{
			int num = 0;
			int num2 = 0;
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				if (char.IsLetter(c))
				{
					num++;
					if (c >= 'Ѐ' && c <= 'ӿ')
					{
						num2++;
					}
				}
			}
			return num >= 3 && num2 * 2 >= num;
		}

		private static TranslationProviderResult Convert(DeepLBatchResult r)
		{
			if (r == null)
			{
				return new TranslationProviderResult
				{
					Error = "Provider returned null."
				};
			}
			return new TranslationProviderResult
			{
				Success = r.Success,
				Translations = (r.Translations ?? new List<string>()),
				Error = r.Error,
				StatusCode = r.StatusCode,
				Attempts = r.Attempts,
				CharacterCount = r.CharacterCount,
				RawResponses = (r.RawResponses ?? new List<string>())
			};
		}

		private static string CodeForDeepL(string language)
		{
			return language;
		}
	}
	public sealed class CachedTranslation
	{
		public string ModName;

		public string ModPackageId;

		public string Type;

		public string DefType;

		public string Key;

		public string SourceLanguage;

		public string SourceText;

		public string OriginalSourceText;

		public string TargetLanguage;

		public string Translation;

		public string AutomaticTranslation;

		public string FilePath;
	}
	public sealed class TranslationCache
	{
		private readonly object gate = new object();

		private readonly Dictionary<string, CachedTranslation> items = new Dictionary<string, CachedTranslation>(StringComparer.Ordinal);

		private readonly List<CachedTranslation> quarantine = new List<CachedTranslation>();

		private int version;

		private int countVersion = -1;

		private int countValue;

		private readonly Stopwatch countClock = Stopwatch.StartNew();

		private long countStamp = -10000L;

		private Dictionary<string, CachedTranslation> sourceIndex;

		public int LastRejected;

		public int Count
		{
			get
			{
				long elapsedMilliseconds = countClock.ElapsedMilliseconds;
				if (countVersion == version || elapsedMilliseconds - countStamp < 1000)
				{
					return countValue;
				}
				lock (gate)
				{
					HashSet<CachedTranslation> hashSet = new HashSet<CachedTranslation>();
					foreach (CachedTranslation value in items.Values)
					{
						if (value != null)
						{
							hashSet.Add(value);
						}
					}
					countValue = hashSet.Count;
					countVersion = version;
					countStamp = elapsedMilliseconds;
					return countValue;
				}
			}
		}

		private void TouchLocked()
		{
			version++;
			sourceIndex = null;
		}

		private static string SourceIndexKey(string sourceText, string target)
		{
			return UrtOptEngine.CollapseWs(sourceText ?? "") + "\n" + LanguageCodeMapper.Canonical(target);
		}

		private static bool IsManualEntry(CachedTranslation x)
		{
			if (x == null || string.IsNullOrWhiteSpace(x.Translation))
			{
				return false;
			}
			return string.IsNullOrWhiteSpace(x.AutomaticTranslation) || !string.Equals(x.Translation.Trim(), x.AutomaticTranslation.Trim(), StringComparison.Ordinal);
		}

		public static bool IsLiveEntry(CachedTranslation x)
		{
			if (x == null)
			{
				return false;
			}
			return string.Equals(x.DefType, "Live", StringComparison.OrdinalIgnoreCase) || string.Equals(x.ModPackageId, "urt.live", StringComparison.OrdinalIgnoreCase);
		}

		private static string Id(string mod, string type, string defType, string key, string target)
		{
			return mod + "\n" + type + "\n" + defType + "\n" + key + "\n" + target;
		}

		private static string IdPair(string mod, string type, string defType, string key, string sourceLang, string target)
		{
			return mod + "\n" + type + "\n" + defType + "\n" + key + "\n" + LanguageCodeMapper.Canonical(sourceLang) + "\n" + LanguageCodeMapper.Canonical(target);
		}

		private static string Id(CachedTranslation x)
		{
			return IdPair(x.ModPackageId, x.Type, x.DefType, x.Key, x.SourceLanguage, x.TargetLanguage);
		}

		public void Clear()
		{
			lock (gate)
			{
				items.Clear();
				quarantine.Clear();
				TouchLocked();
			}
		}

		public bool TryGet(string mod, string type, string defType, string key, string target, out CachedTranslation value)
		{
			return TryGet(mod, type, defType, key, null, target, out value);
		}

		public bool TryGet(string mod, string type, string defType, string key, string sourceLang, string target, out CachedTranslation value)
		{
			lock (gate)
			{
				if (!string.IsNullOrWhiteSpace(sourceLang) && items.TryGetValue(IdPair(mod, type, defType, key, sourceLang, target), out value))
				{
					return true;
				}
				return items.TryGetValue(Id(mod, type, defType, key, target), out value);
			}
		}

		public static bool IsUsableTranslation(CachedTranslation x, string sourceText)
		{
			if (x == null)
			{
				return false;
			}
			string text = ((!string.IsNullOrWhiteSpace(x.Translation)) ? x.Translation : x.AutomaticTranslation);
			text = LanguageDetector.RepairMarkup(text);
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			string text2 = (sourceText ?? x.SourceText ?? "").Trim();
			string text3 = text.Trim();
			if (text3.IndexOf("[[#", StringComparison.Ordinal) >= 0)
			{
				return false;
			}
			if (LanguageDetector.HasBrokenMarkup(text3))
			{
				return false;
			}
			if (!IsManualEntry(x) && UrtResultValidator.LooksSpliced(text2, text3))
			{
				return false;
			}
			if (!UrtResultValidator.RulePrefixKept(text2, text3))
			{
				return false;
			}
			string text4 = LanguageCodeMapper.Canonical(x.TargetLanguage);
			if (string.IsNullOrEmpty(text4))
			{
				text4 = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			}
			if (IsLiveEntry(x) || IsManualEntry(x))
			{
				if (text2.Length > 0 && string.Equals(text3, text2, StringComparison.OrdinalIgnoreCase))
				{
					return IsManualEntry(x);
				}
				return !LanguageDetector.HasResidualForeignScript(text3, text4);
			}
			if (text2.Length > 0 && string.Equals(text3, text2, StringComparison.OrdinalIgnoreCase))
			{
				return LanguageDetector.LooksLikeProperNameOrCode(text2) || (LanguageDetector.LooksLikeTargetText(text2, text4) && !LanguageDetector.IsHalfTranslated(text2, text4));
			}
			return UrtResultValidator.Judge(text2, text3, text4) == null;
		}

		private static int WordCount(string text)
		{
			int num = 0;
			bool flag = false;
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				bool flag2 = char.IsLetter(c);
				if (flag2 && !flag)
				{
					num++;
				}
				flag = flag2;
			}
			return num;
		}

		public bool IsReallyCached(string mod, string type, string defType, string key, string sourceText, string target)
		{
			if (TryGet(mod, type, defType, key, target, out var value) && IsUsableTranslation(value, sourceText))
			{
				return true;
			}
			if (!string.IsNullOrWhiteSpace(sourceText) && UrtOptEngine.TryRam(sourceText, LanguageDetector.DetectEmbeddedSourceLanguage(sourceText), target, out var translation) && IsGoodStored(sourceText, translation, target))
			{
				return true;
			}
			return false;
		}

		public static bool IsGoodStored(string sourceText, string translation, string target)
		{
			if (string.IsNullOrWhiteSpace(translation))
			{
				return false;
			}
			string text = (sourceText ?? "").Trim();
			string text2 = translation.Trim();
			if (text.Length > 0 && string.Equals(text2, text, StringComparison.OrdinalIgnoreCase) && !LanguageDetector.LooksLikeProperNameOrCode(text))
			{
				return false;
			}
			CachedTranslation x = new CachedTranslation
			{
				SourceText = text,
				Translation = text2,
				AutomaticTranslation = text2,
				TargetLanguage = target
			};
			return IsUsableTranslation(x, text);
		}

		public HashSet<string> BuildTranslatedSourceSet(string target)
		{
			string b = LanguageCodeMapper.Canonical(target);
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			lock (gate)
			{
				foreach (CachedTranslation value in items.Values)
				{
					if (value != null && IsUsableTranslation(value, value.SourceText) && string.Equals(LanguageCodeMapper.Canonical(value.TargetLanguage), b, StringComparison.OrdinalIgnoreCase))
					{
						string text = (value.SourceText ?? "").Trim();
						if (text.Length > 0)
						{
							hashSet.Add(text);
						}
					}
				}
			}
			return hashSet;
		}

		public bool Put(CachedTranslation value)
		{
			return PutCore(value, validate: true);
		}

		private bool PutCore(CachedTranslation value, bool validate)
		{
			if (value == null)
			{
				return false;
			}
			Compact(value);
			if (string.IsNullOrWhiteSpace(value.Key))
			{
				string text = value.ModPackageId + "|" + (value.SourceText ?? value.Translation ?? "");
				value.Key = "runtime.label." + UrtHash.Of64(text);
				if (string.IsNullOrWhiteSpace(value.DefType))
				{
					value.DefType = "Runtime";
				}
			}
			if (IsNonTranslatable(value.SourceText))
			{
				return false;
			}
			if (string.IsNullOrWhiteSpace(value.Translation) && string.IsNullOrWhiteSpace(value.AutomaticTranslation))
			{
				return false;
			}
			if (validate && !IsManualEntry(value) && !IsUsableTranslation(value, value.SourceText))
			{
				return false;
			}
			value.Translation = LanguageDetector.RepairMarkup(value.Translation);
			value.AutomaticTranslation = LanguageDetector.RepairMarkup(value.AutomaticTranslation);
			if (value.AutomaticTranslation == null)
			{
				value.AutomaticTranslation = "";
			}
			if (value.FilePath == null)
			{
				value.FilePath = "";
			}
			lock (gate)
			{
				if (items.TryGetValue(Id(value), out var value2) && value2 != null && !string.IsNullOrWhiteSpace(value2.OriginalSourceText))
				{
					value.OriginalSourceText = value2.OriginalSourceText;
				}
				else if (string.IsNullOrWhiteSpace(value.OriginalSourceText))
				{
					value.OriginalSourceText = value.SourceText ?? "";
				}
				items[Id(value)] = value;
				items[Id(value.ModPackageId, value.Type, value.DefType, value.Key, LanguageCodeMapper.Canonical(value.TargetLanguage))] = value;
				TouchLocked();
			}
			string translation = ((!string.IsNullOrWhiteSpace(value.Translation)) ? value.Translation : value.AutomaticTranslation);
			UrtOptEngine.PutRam(value.SourceText, value.SourceLanguage, value.TargetLanguage, translation);
			return true;
		}

		public bool TryGetBySource(string sourceText, string target, out string translation)
		{
			translation = null;
			if (UrtOptEngine.TryRam(sourceText, target, out translation))
			{
				return true;
			}
			CachedTranslation value2;
			lock (gate)
			{
				if (sourceIndex == null)
				{
					sourceIndex = new Dictionary<string, CachedTranslation>(StringComparer.Ordinal);
					foreach (CachedTranslation value3 in items.Values)
					{
						if (value3 == null)
						{
							continue;
						}
						string value = ((!string.IsNullOrWhiteSpace(value3.Translation)) ? value3.Translation : value3.AutomaticTranslation);
						if (!string.IsNullOrWhiteSpace(value))
						{
							string key = SourceIndexKey(value3.SourceText, value3.TargetLanguage);
							if (!sourceIndex.ContainsKey(key))
							{
								sourceIndex[key] = value3;
							}
						}
					}
				}
				sourceIndex.TryGetValue(SourceIndexKey(sourceText, target), out value2);
			}
			if (value2 == null)
			{
				return false;
			}
			translation = ((!string.IsNullOrWhiteSpace(value2.Translation)) ? value2.Translation : value2.AutomaticTranslation);
			UrtOptEngine.PutRam(sourceText, LanguageDetector.DetectEmbeddedSourceLanguage(sourceText), target, translation);
			return true;
		}

		public List<CachedTranslation> GetAll()
		{
			lock (gate)
			{
				HashSet<CachedTranslation> hashSet = new HashSet<CachedTranslation>();
				List<CachedTranslation> list = new List<CachedTranslation>();
				foreach (CachedTranslation value in items.Values)
				{
					if (value != null && hashSet.Add(value))
					{
						list.Add(value);
					}
				}
				return list;
			}
		}

		public bool Remove(string modPackageId, string type, string defType, string key, string targetLanguage)
		{
			lock (gate)
			{
				bool result = items.Remove(Id(modPackageId, type, defType, key, targetLanguage));
				TouchLocked();
				return result;
			}
		}

		public int RemoveForMod(string modName, string modPackageId = null, string targetLanguage = null)
		{
			if (string.IsNullOrWhiteSpace(modName) && string.IsNullOrWhiteSpace(modPackageId))
			{
				return 0;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			List<string> list = new List<string>();
			lock (gate)
			{
				foreach (KeyValuePair<string, CachedTranslation> item in items)
				{
					CachedTranslation value = item.Value;
					if (value != null && ((!string.IsNullOrWhiteSpace(modName) && string.Equals(value.ModName, modName, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(modPackageId) && string.Equals(value.ModPackageId, modPackageId, StringComparison.OrdinalIgnoreCase))) && (string.IsNullOrEmpty(target) || string.Equals(LanguageCodeMapper.Canonical(value.TargetLanguage), target, StringComparison.OrdinalIgnoreCase)))
					{
						list.Add(item.Key);
					}
				}
				foreach (string item2 in list)
				{
					items.Remove(item2);
				}
				quarantine.RemoveAll((CachedTranslation x) => x != null && ((!string.IsNullOrWhiteSpace(modName) && string.Equals(x.ModName, modName, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(modPackageId) && string.Equals(x.ModPackageId, modPackageId, StringComparison.OrdinalIgnoreCase))) && (string.IsNullOrEmpty(target) || string.Equals(LanguageCodeMapper.Canonical(x.TargetLanguage), target, StringComparison.OrdinalIgnoreCase)));
				TouchLocked();
			}
			return list.Count;
		}

		public int RemoveInvalidAutomaticTranslations()
		{
			List<string> list = new List<string>();
			lock (gate)
			{
				foreach (KeyValuePair<string, CachedTranslation> item in items)
				{
					CachedTranslation value = item.Value;
					if (value != null)
					{
						string text = NormalizeForComparison(value.SourceText);
						string text2 = NormalizeForComparison(value.AutomaticTranslation);
						string text3 = LanguageCodeMapper.Canonical(value.TargetLanguage);
						string text4 = LanguageCodeMapper.Canonical(value.SourceLanguage);
						if (!string.IsNullOrEmpty(text) && string.Equals(text, text2, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(text4) && !string.Equals(text4, text3, StringComparison.OrdinalIgnoreCase))
						{
							list.Add(item.Key);
						}
						else if (!IsManualEntry(value) && !string.IsNullOrWhiteSpace(text2) && LanguageDetector.IsHalfTranslated(text2, text3))
						{
							list.Add(item.Key);
						}
					}
				}
				foreach (string item2 in list)
				{
					items.Remove(item2);
				}
				int num = quarantine.RemoveAll((CachedTranslation x) => !IsManualEntry(x));
				TouchLocked();
				return list.Count + num;
			}
		}

		public int OptimizeJunk()
		{
			List<string> list = new List<string>();
			lock (gate)
			{
				foreach (KeyValuePair<string, CachedTranslation> item in items)
				{
					CachedTranslation value = item.Value;
					if (value == null)
					{
						list.Add(item.Key);
						continue;
					}
					string text = (value.Translation ?? "").Trim();
					string text2 = (value.AutomaticTranslation ?? "").Trim();
					string text3 = ((text.Length > 0) ? text : text2);
					if (text3.Length == 0 || IsJunkTranslation(text3))
					{
						list.Add(item.Key);
					}
				}
				foreach (string item2 in list)
				{
					items.Remove(item2);
				}
				TouchLocked();
			}
			return list.Count;
		}

		private static bool IsJunkTranslation(string t)
		{
			if (string.IsNullOrWhiteSpace(t))
			{
				return true;
			}
			if (t.IndexOf("LANGPAIR", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (t.IndexOf("INVALID SOURCE", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (t.IndexOf("QUERY LENGTH", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (t.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (t.Equals("UTR", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (t == "{0}" || t == "{1}" || t == "{2}")
			{
				return true;
			}
			return false;
		}

		private static string NormalizeForComparison(string value)
		{
			return (value ?? "").Trim().Replace("\r\n", "\n").Replace("\r", "\n");
		}

		public void Save(string path)
		{
			SaveFiltered(path, liveOnly: false);
		}

		public void SaveLive(string path)
		{
			SaveFiltered(path, liveOnly: true);
		}

		// b114: one writer at a time; SaveCache (Task.Run), FlushLiveCache (ThreadPool) and the editor could
		// write the same "<file>.tmp" concurrently and corrupt it or fail on File.Replace
		private static readonly object FileWriteGate = new object();

		private void SaveFiltered(string path, bool liveOnly)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return;
			}
			lock (FileWriteGate)
			{
				SaveFilteredLocked(path, liveOnly);
			}
		}

		private void SaveFilteredLocked(string path, bool liveOnly)
		{
			List<CachedTranslation> all = GetAll();
			string directoryName = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			string text = path + ".tmp";
			try
			{
				using (XmlWriter xmlWriter = XmlWriter.Create(text, new XmlWriterSettings
				{
					Indent = true
				}))
				{
					xmlWriter.WriteStartElement("TranslationCache");
					for (int i = 0; i < all.Count; i++)
					{
						CachedTranslation cachedTranslation = all[i];
						if (cachedTranslation != null && (!string.IsNullOrWhiteSpace(cachedTranslation.Translation) || !string.IsNullOrWhiteSpace(cachedTranslation.AutomaticTranslation)))
						{
							bool flag = IsLiveEntry(cachedTranslation);
							if (liveOnly == flag)
							{
								xmlWriter.WriteStartElement("Entry");
								W(xmlWriter, "ModName", cachedTranslation.ModName);
								W(xmlWriter, "ModPackageId", cachedTranslation.ModPackageId);
								W(xmlWriter, "Type", cachedTranslation.Type);
								W(xmlWriter, "DefType", cachedTranslation.DefType);
								W(xmlWriter, "Key", cachedTranslation.Key);
								W(xmlWriter, "SourceLanguage", cachedTranslation.SourceLanguage);
								W(xmlWriter, "SourceText", cachedTranslation.SourceText);
								W(xmlWriter, "OriginalSourceText", cachedTranslation.OriginalSourceText);
								W(xmlWriter, "TargetLanguage", cachedTranslation.TargetLanguage);
								W(xmlWriter, "Translation", cachedTranslation.Translation);
								W(xmlWriter, "AutomaticTranslation", cachedTranslation.AutomaticTranslation);
								W(xmlWriter, "FilePath", cachedTranslation.FilePath);
								xmlWriter.WriteEndElement();
							}
						}
					}
					xmlWriter.WriteEndElement();
				}
				if (File.Exists(path))
				{
					File.Replace(text, path, path + ".bak");
				}
				else
				{
					File.Move(text, path);
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] Cache save failed (" + Path.GetFileName(path) + "): " + ex.GetType().Name + " " + ex.Message);
				try
				{
					if (File.Exists(text))
					{
						File.Delete(text);
					}
				}
				catch
				{
				}
			}
		}

		public void Load(string path)
		{
			Load(path, clearFirst: true);
		}

		public void Load(string path, bool clearFirst)
		{
			LastRejected = 0;
			if (clearFirst)
			{
				lock (gate)
				{
					items.Clear();
					quarantine.Clear();
					TouchLocked();
				}
			}
			if (!File.Exists(path))
			{
				return;
			}
			// b115: the cache is streamed (XmlReader) instead of loaded as a whole
			// XmlDocument. A 100 MB cache used to need ~1 GB while loading, and the
			// game's memory never shrinks again – every later garbage collection was
			// slower and the game hitched or froze. Repeated texts (mod, file, type,
			// language) are also shared instead of stored 120 000 times.
			List<CachedTranslation> loaded = new List<CachedTranslation>();
			try
			{
				StreamEntries(path, loaded);
			}
			catch (Exception ex)
			{
				string text = path + ".bak";
				MLog.Warn("[URT] Cache file " + Path.GetFileName(path) + " is damaged (" + ex.Message + "); trying " + Path.GetFileName(text) + ".");
				try
				{
					File.Copy(path, path + ".damaged", overwrite: true);
				}
				catch
				{
				}
				if (!File.Exists(text))
				{
					return;
				}
				loaded.Clear();
				try
				{
					StreamEntries(text, loaded);
				}
				catch (Exception ex2)
				{
					MLog.Warn("[URT] Cache backup is damaged too: " + ex2.Message);
					return;
				}
			}
			for (int li = 0; li < loaded.Count; li++)
			{
				CachedTranslation cachedTranslation = loaded[li];
				if (!IsManualEntry(cachedTranslation) && !IsUsableTranslation(cachedTranslation, cachedTranslation.SourceText))
				{
					LastRejected++;
					if (!string.IsNullOrWhiteSpace(cachedTranslation.Translation) || !string.IsNullOrWhiteSpace(cachedTranslation.AutomaticTranslation))
					{
						lock (gate)
						{
							quarantine.Add(cachedTranslation);
						}
					}
				}
				else
				{
					PutCore(cachedTranslation, validate: false);
				}
			}
			loaded = null;
			PurgeBrokenMasks();
			if (LastRejected > 0)
			{
				MLog.Info("[URT] Cache load rejected " + LastRejected + " unusable/half-translated entries; they will be retried.");
			}
		}

		public List<CachedTranslation> PurgeDirtyForRetranslate(string targetLanguage)
		{
			string text = LanguageCodeMapper.Canonical(targetLanguage);
			if (string.IsNullOrEmpty(text))
			{
				text = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			}
			List<CachedTranslation> list = new List<CachedTranslation>();
			List<string> list2 = new List<string>();
			lock (gate)
			{
				foreach (KeyValuePair<string, CachedTranslation> item in items)
				{
					CachedTranslation value = item.Value;
					if (value == null)
					{
						list2.Add(item.Key);
					}
					else if (!IsManualEntry(value) && (string.IsNullOrEmpty(text) || string.Equals(LanguageCodeMapper.Canonical(value.TargetLanguage), text, StringComparison.OrdinalIgnoreCase)) && IsDirtyAutomatic(value, text))
					{
						list2.Add(item.Key);
						list.Add(value);
					}
				}
				for (int i = 0; i < list2.Count; i++)
				{
					items.Remove(list2[i]);
				}
				for (int num = quarantine.Count - 1; num >= 0; num--)
				{
					CachedTranslation cachedTranslation = quarantine[num];
					if (cachedTranslation != null && (string.IsNullOrEmpty(text) || string.Equals(LanguageCodeMapper.Canonical(cachedTranslation.TargetLanguage), text, StringComparison.OrdinalIgnoreCase)) && (IsDirtyAutomatic(cachedTranslation, text) || !IsUsableTranslation(cachedTranslation, cachedTranslation.SourceText)))
					{
						list.Add(cachedTranslation);
						quarantine.RemoveAt(num);
					}
				}
				if (list.Count > 0)
				{
					TouchLocked();
				}
			}
			return list;
		}

		public static bool IsDirtyAutomatic(CachedTranslation x, string targetLanguage)
		{
			if (x == null)
			{
				return true;
			}
			string text = LanguageCodeMapper.Canonical(targetLanguage);
			if (string.IsNullOrEmpty(text))
			{
				text = LanguageCodeMapper.Canonical(x.TargetLanguage);
			}
			string text2 = (x.SourceText ?? x.OriginalSourceText ?? "").Trim();
			string text3 = ((!string.IsNullOrWhiteSpace(x.Translation)) ? x.Translation : x.AutomaticTranslation);
			text3 = LanguageDetector.RepairMarkup(text3);
			if (string.IsNullOrWhiteSpace(text3))
			{
				return true;
			}
			text3 = text3.Trim();
			if (IsNonTranslatable(text2) || LanguageDetector.LooksLikeProperNameOrCode(text2))
			{
				return false;
			}
			if (UrtResultValidator.LooksLikeLeakedMask(text3) || LanguageDetector.HasBrokenMarkup(text3))
			{
				return true;
			}
			if (text2.Length > 0 && string.Equals(text3, text2, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (LanguageDetector.IsMixedLanguage(text3, text))
			{
				return true;
			}
			if (LanguageDetector.IsHalfTranslated(text3, text))
			{
				return true;
			}
			if (LanguageDetector.HasResidualForeignScript(text3, text))
			{
				return true;
			}
			if (!LanguageDetector.IsTranslationComplete(text2, text3, text))
			{
				return true;
			}
			return !IsUsableTranslation(x, text2);
		}

		public int PurgeBrokenMasks()
		{
			List<string> list = new List<string>();
			bool flag = string.Equals(LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage()), "Czech", StringComparison.OrdinalIgnoreCase);
			lock (gate)
			{
				foreach (KeyValuePair<string, CachedTranslation> item in items)
				{
					string text = ((item.Value != null) ? item.Value.Translation : null);
					if (!string.IsNullOrEmpty(text) && (UrtResultValidator.LooksLikeLeakedMask(text) || (flag && (text.IndexOf("zástupný symbol", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("zastupny symbol", StringComparison.OrdinalIgnoreCase) >= 0))))
					{
						list.Add(item.Key);
					}
				}
				for (int i = 0; i < list.Count; i++)
				{
					items.Remove(list[i]);
				}
				if (list.Count > 0)
				{
					TouchLocked();
				}
			}
			return list.Count;
		}

		private static bool IsNonTranslatable(string value)
		{
			string text = (value ?? "").Trim();
			if (string.IsNullOrEmpty(text))
			{
				return true;
			}
			if (Regex.IsMatch(text, "^\\$[A-Za-z_][A-Za-z0-9_.]*$"))
			{
				return true;
			}
			if (Regex.IsMatch(text, "^\\{[0-9]+\\}$"))
			{
				return true;
			}
			if (Regex.IsMatch(text, "^\\\\(?:r\\\\n|n|r|t)$"))
			{
				return true;
			}
			if (text.StartsWith("<", StringComparison.Ordinal) && text.EndsWith(">", StringComparison.Ordinal))
			{
				string text2 = Regex.Replace(text, "<[^<>]*>", "");
				bool flag = false;
				for (int i = 0; i < text2.Length; i++)
				{
					if (char.IsLetter(text2[i]))
					{
						flag = true;
						break;
					}
				}
				if (!flag)
				{
					return true;
				}
			}
			for (int j = 0; j < text.Length; j++)
			{
				if (char.IsLetterOrDigit(text[j]))
				{
					return false;
				}
			}
			return true;
		}

		private static void W(XmlWriter w, string n, string v)
		{
			w.WriteElementString(n, XmlSafe(v));
		}

		private static string XmlSafe(string v)
		{
			if (string.IsNullOrEmpty(v))
			{
				return "";
			}
			StringBuilder stringBuilder = null;
			for (int i = 0; i < v.Length; i++)
			{
				char c = v[i];
				if (char.IsHighSurrogate(c) && i + 1 < v.Length && char.IsLowSurrogate(v[i + 1]))
				{
					if (stringBuilder != null)
					{
						stringBuilder.Append(c);
						stringBuilder.Append(v[i + 1]);
					}
					i++;
				}
				else if (XmlConvert.IsXmlChar(c))
				{
					stringBuilder?.Append(c);
				}
				else if (stringBuilder == null)
				{
					stringBuilder = new StringBuilder(v.Length);
					stringBuilder.Append(v, 0, i);
				}
			}
			return (stringBuilder == null) ? v : stringBuilder.ToString();
		}

		private static string R(XmlNode n, string name)
		{
			XmlNode xmlNode = n.SelectSingleNode(name);
			return (xmlNode == null) ? "" : xmlNode.InnerText;
		}

		// b115: shared copies of texts that repeat in almost every entry
		private static readonly Dictionary<string, string> SharedText = new Dictionary<string, string>(StringComparer.Ordinal);

		public static string Shared(string value)
		{
			if (value == null)
			{
				return null;
			}
			if (value.Length == 0)
			{
				return "";
			}
			lock (SharedText)
			{
				if (SharedText.TryGetValue(value, out string existing))
				{
					return existing;
				}
				SharedText[value] = value;
				return value;
			}
		}

		public static void Compact(CachedTranslation c)
		{
			if (c == null)
			{
				return;
			}
			c.ModName = Shared(c.ModName);
			c.ModPackageId = Shared(c.ModPackageId);
			c.Type = Shared(c.Type);
			c.DefType = Shared(c.DefType);
			c.SourceLanguage = Shared(c.SourceLanguage);
			c.TargetLanguage = Shared(c.TargetLanguage);
			c.FilePath = Shared(c.FilePath);
			if (c.AutomaticTranslation != null && c.Translation != null && !ReferenceEquals(c.AutomaticTranslation, c.Translation) && string.Equals(c.AutomaticTranslation, c.Translation, StringComparison.Ordinal))
			{
				c.AutomaticTranslation = c.Translation;
			}
			if (c.OriginalSourceText != null && c.SourceText != null && !ReferenceEquals(c.OriginalSourceText, c.SourceText) && string.Equals(c.OriginalSourceText, c.SourceText, StringComparison.Ordinal))
			{
				c.OriginalSourceText = c.SourceText;
			}
		}

		private static void StreamEntries(string path, List<CachedTranslation> into)
		{
			XmlReaderSettings settings = new XmlReaderSettings
			{
				IgnoreWhitespace = true,
				IgnoreComments = true,
				IgnoreProcessingInstructions = true
			};
			using (XmlReader r = XmlReader.Create(path, settings))
			{
				while (r.Read())
				{
					if (r.NodeType != XmlNodeType.Element || r.LocalName != "Entry" || r.Depth != 1)
					{
						continue;
					}
					CachedTranslation c = new CachedTranslation
					{
						ModName = "", ModPackageId = "", Type = "", DefType = "", Key = "", SourceLanguage = "",
						SourceText = "", OriginalSourceText = "", TargetLanguage = "", Translation = "", AutomaticTranslation = "", FilePath = ""
					};
					if (r.IsEmptyElement)
					{
						into.Add(c);
						continue;
					}
					int depth = r.Depth;
					r.Read();
					while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
					{
						if (r.NodeType == XmlNodeType.Element)
						{
							string name = r.LocalName;
							string val = r.ReadElementContentAsString();
							switch (name)
							{
							case "ModName": c.ModName = val; break;
							case "ModPackageId": c.ModPackageId = val; break;
							case "Type": c.Type = val; break;
							case "DefType": c.DefType = val; break;
							case "Key": c.Key = val; break;
							case "SourceLanguage": c.SourceLanguage = val; break;
							case "SourceText": c.SourceText = val; break;
							case "OriginalSourceText": c.OriginalSourceText = val; break;
							case "TargetLanguage": c.TargetLanguage = val; break;
							case "Translation": c.Translation = val; break;
							case "AutomaticTranslation": c.AutomaticTranslation = val; break;
							case "FilePath": c.FilePath = val; break;
							}
						}
						else
						{
							r.Read();
						}
					}
					Compact(c);
					into.Add(c);
				}
			}
		}
	}
	public sealed class TranslationDatabase
	{
		public sealed class IncrementalInventoryBuilder
		{
			private readonly Dictionary<string, string> mainByMod;

			private readonly List<TranslationEntry> entries;

			private readonly List<string> allowedSources;

			private readonly string target;

			private readonly TranslationCache skipCache;

			private readonly Dictionary<string, InventoryState> states = new Dictionary<string, InventoryState>(StringComparer.Ordinal);

			private readonly TranslationInventory inventory = new TranslationInventory();

			private List<InventoryState> stateList;

			private int index;

			private int phase;

			public const int DefaultCandidateCap = 200000;

			private readonly int candidateCap;

			public int TotalEntries => entries.Count;

			public int Processed => (phase == 0) ? index : (entries.Count + ((stateList != null) ? index : 0));

			public bool Finished => phase >= 2;

			public IncrementalInventoryBuilder(IEnumerable<TranslationEntry> sourceEntries, string targetLanguage, IEnumerable<string> sourceLanguages, TranslationCache cache = null, Dictionary<string, string> mainLanguageByMod = null)
			{
				entries = (sourceEntries as List<TranslationEntry>) ?? new List<TranslationEntry>(sourceEntries ?? Enumerable.Empty<TranslationEntry>());
				allowedSources = PreferredOrder(sourceLanguages);
				mainByMod = mainLanguageByMod;
				target = LanguageCodeMapper.Canonical(targetLanguage);
				skipCache = cache;
				candidateCap = (UniversalRimTranslatorSettings.UseWideCoverage() ? 200000 : 80000);
			}

			public bool Step(int budget)
			{
				if (phase >= 2)
				{
					return true;
				}
				if (budget < 1)
				{
					budget = 1;
				}
				if (phase == 0)
				{
					int num = Math.Min(entries.Count, index + budget);
					while (index < num)
					{
						TranslationEntry translationEntry = entries[index];
						if (translationEntry != null)
						{
							string key = translationEntry.ModPackageId + "\n" + translationEntry.Type + "\n" + translationEntry.DefType + "\n" + translationEntry.Key;
							if (!states.TryGetValue(key, out var value))
							{
								value = new InventoryState();
								states[key] = value;
							}
							string text = translationEntry.NormalizedLanguage;
							if (string.IsNullOrEmpty(text))
							{
								text = "English";
							}
							bool languagePack = IsLanguagePackFile(translationEntry.FilePath);
							if (string.Equals(text, target, StringComparison.OrdinalIgnoreCase))
							{
								if (value.Target == null)
								{
									value.Target = translationEntry;
								}
							}
							else
							{
								int num2 = SourceRankFor(text, languagePack, allowedSources);
								if (mainByMod != null && translationEntry.ModPackageId != null && mainByMod.TryGetValue(translationEntry.ModPackageId, out var value2))
								{
									if (string.Equals(LanguageCodeMapper.Canonical(text), value2, StringComparison.OrdinalIgnoreCase))
									{
										num2 = 0;
									}
									else if (num2 >= 0)
									{
										num2 = 100000 + num2;
									}
								}
								if (num2 >= 0 && (value.Source == null || num2 < value.SourceRank))
								{
									value.Source = translationEntry;
									value.SourceRank = num2;
								}
							}
						}
						index++;
					}
					if (index >= entries.Count)
					{
						stateList = new List<InventoryState>(states.Values);
						states.Clear();
						index = 0;
						phase = 1;
					}
					return false;
				}
				if (phase == 1)
				{
					int num3 = Math.Min(stateList.Count, index + budget);
					while (index < num3)
					{
						InventoryState inventoryState = stateList[index];
						inventory.TotalKeys++;
						if (inventoryState.Target != null && inventoryState.Source != null && !SameLooseText(inventoryState.Target.Text, inventoryState.Source.Text))
						{
							inventory.AlreadyTarget++;
						}
						else if (inventoryState.Target != null && inventoryState.Source == null)
						{
							inventory.AlreadyTarget++;
						}
						else
						{
							inventory.MissingTarget++;
							TranslationEntry source = inventoryState.Source;
							if (source != null && IsQueueText(source) && (skipCache == null || !skipCache.IsReallyCached(source.ModPackageId, source.Type, source.DefType, source.Key, source.Text, target)))
							{
								inventory.Candidates.Add(new TranslationCandidate
								{
									ModName = source.ModName,
									ModPackageId = source.ModPackageId,
									Type = source.Type,
									DefType = source.DefType,
									Key = source.Key,
									SourceLanguage = SourceLanguageOf(source),
									SourceText = source.Text,
									FilePath = source.FilePath
								});
								if (inventory.Candidates.Count >= candidateCap)
								{
									index = stateList.Count;
									break;
								}
							}
						}
						index++;
					}
					if (index >= stateList.Count)
					{
						phase = 2;
						return true;
					}
				}
				return phase >= 2;
			}

			public TranslationInventory Finish()
			{
				while (!Step(int.MaxValue))
				{
				}
				return inventory;
			}
		}

		private sealed class InventoryState
		{
			public TranslationEntry Target;

			public TranslationEntry Source;

			public int SourceRank = int.MaxValue;
		}

		private Dictionary<string, TranslationEntry> entries = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);

		public int Count => entries.Count;

		public void Clear()
		{
			entries.Clear();
		}

		public void ReplaceWith(TranslationDatabase other)
		{
			if (other == null)
			{
				throw new ArgumentNullException("other");
			}
			entries = other.entries;
			other.entries = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
		}

		public bool HasPackageLanguage(string packageId, string language)
		{
			foreach (TranslationEntry value in entries.Values)
			{
				if (string.Equals(value.ModPackageId, packageId, StringComparison.OrdinalIgnoreCase) && string.Equals(value.NormalizedLanguage, LanguageNormalizer.Normalize(language), StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		public void Add(TranslationEntry entry)
		{
			if (entry != null && !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Text))
			{
				entry.NormalizedLanguage = LanguageNormalizer.Normalize(entry.Language);
				string key = entry.NormalizedLanguage + "\n" + entry.ModPackageId + "\n" + entry.Type + "\n" + entry.DefType + "\n" + entry.Key;
				if (!entries.ContainsKey(key))
				{
					entries[key] = entry;
				}
			}
		}

		public List<TranslationEntry> GetEntries()
		{
			return new List<TranslationEntry>(entries.Values);
		}

		public TranslationInventory BuildInventory(string targetLanguage, string preferredSourceLanguage)
		{
			return BuildInventory(targetLanguage, new string[1] { preferredSourceLanguage });
		}

		public TranslationInventory BuildInventory(string targetLanguage, IEnumerable<string> sourceLanguages)
		{
			return BuildInventory(entries.Values, targetLanguage, sourceLanguages);
		}

		public static TranslationInventory BuildInventory(IEnumerable<TranslationEntry> sourceEntries, string targetLanguage, IEnumerable<string> sourceLanguages)
		{
			IncrementalInventoryBuilder incrementalInventoryBuilder = new IncrementalInventoryBuilder(sourceEntries, targetLanguage, sourceLanguages);
			while (!incrementalInventoryBuilder.Step(int.MaxValue))
			{
			}
			return incrementalInventoryBuilder.Finish();
		}

		public static Dictionary<string, string> DetectMainLanguages(Dictionary<string, ModScanner.LanguageStats> stats, out Dictionary<string, int> modsPerLanguage)
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (stats != null)
			{
				foreach (KeyValuePair<string, ModScanner.LanguageStats> stat in stats)
				{
					ModScanner.LanguageStats value = stat.Value;
					string text = null;
					if (value.Defs.Count > 0)
					{
						text = Most(value.Defs);
					}
					else if (value.Packs.ContainsKey("English"))
					{
						text = "English";
					}
					else if (value.Packs.Count > 0)
					{
						text = Most(value.Packs);
					}
					if (text != null)
					{
						dictionary[stat.Key] = text;
					}
				}
			}
			modsPerLanguage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (string value3 in dictionary.Values)
			{
				modsPerLanguage.TryGetValue(value3, out var value2);
				modsPerLanguage[value3] = value2 + 1;
			}
			return dictionary;
		}

		private static string Most(Dictionary<string, int> counts)
		{
			string result = null;
			int num = -1;
			foreach (KeyValuePair<string, int> count in counts)
			{
				if (count.Value > num || (count.Value == num && string.Equals(count.Key, "English", StringComparison.OrdinalIgnoreCase)))
				{
					result = count.Key;
					num = count.Value;
				}
			}
			return result;
		}

		public static Dictionary<string, string> DetectMainLanguages(IList<TranslationEntry> source, out Dictionary<string, int> modsPerLanguage)
		{
			Dictionary<string, Dictionary<string, int>> dictionary = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, Dictionary<string, int>> dictionary2 = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
			foreach (TranslationEntry item in source ?? new List<TranslationEntry>())
			{
				if (item == null || string.IsNullOrEmpty(item.ModPackageId))
				{
					continue;
				}
				string text = item.FilePath ?? "";
				if (!text.StartsWith("<", StringComparison.Ordinal))
				{
					string text2 = LanguageCodeMapper.Canonical(item.NormalizedLanguage ?? "");
					if (string.IsNullOrEmpty(text2))
					{
						text2 = "English";
					}
					Dictionary<string, Dictionary<string, int>> dictionary3 = (IsLanguagePackFile(text) ? dictionary2 : dictionary);
					if (!dictionary3.TryGetValue(item.ModPackageId, out var value))
					{
						value = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
						dictionary3[item.ModPackageId] = value;
					}
					value.TryGetValue(text2, out var value2);
					value[text2] = value2 + 1;
				}
			}
			Dictionary<string, string> dictionary4 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> hashSet = new HashSet<string>(dictionary.Keys, StringComparer.OrdinalIgnoreCase);
			hashSet.UnionWith(dictionary2.Keys);
			foreach (string item2 in hashSet)
			{
				if (!dictionary.TryGetValue(item2, out var value3) || value3.Count == 0)
				{
					dictionary2.TryGetValue(item2, out value3);
				}
				if (value3 == null || value3.Count == 0)
				{
					continue;
				}
				string text3 = null;
				int num = -1;
				foreach (KeyValuePair<string, int> item3 in value3)
				{
					if (item3.Value > num || (item3.Value == num && string.Equals(item3.Key, "English", StringComparison.OrdinalIgnoreCase)))
					{
						text3 = item3.Key;
						num = item3.Value;
					}
				}
				if (text3 != null)
				{
					dictionary4[item2] = text3;
				}
			}
			modsPerLanguage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (string value5 in dictionary4.Values)
			{
				modsPerLanguage.TryGetValue(value5, out var value4);
				modsPerLanguage[value5] = value4 + 1;
			}
			return dictionary4;
		}

		public static List<string> PreferredOrder(IEnumerable<string> languages)
		{
			List<string> list = (languages ?? Enumerable.Empty<string>()).Where((string x) => !string.IsNullOrWhiteSpace(x)).Select(LanguageCodeMapper.Canonical).Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			list.Sort(delegate(string a, string b)
			{
				bool flag = string.Equals(a, "English", StringComparison.OrdinalIgnoreCase);
				bool flag2 = string.Equals(b, "English", StringComparison.OrdinalIgnoreCase);
				return (flag != flag2) ? ((!flag) ? 1 : (-1)) : StringComparer.OrdinalIgnoreCase.Compare(a, b);
			});
			return list;
		}

		private static bool IsQueueText(TranslationEntry source)
		{
			if (source == null)
			{
				return false;
			}
			string text = source.Key ?? "";
			string text2 = (source.FilePath ?? "").Replace('\\', '/');
			if (!HasLetterChar(source.Text))
			{
				return false;
			}
			if (text.IndexOf("rulesStrings", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return UrtResultValidator.RulePrefixOf(source.Text) != null;
			}
			if (text.IndexOf("rulesFiles", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
			if (text2.IndexOf("/RulePacks/", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
			if (text2.IndexOf("/Strings/", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
			if (string.Equals(source.Type, "Keyed", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string[] array = text.Split(new char[1] { '.' });
			for (int num = array.Length - 1; num >= 0; num--)
			{
				string text3 = array[num];
				if (!string.IsNullOrEmpty(text3))
				{
					bool flag = true;
					for (int i = 0; i < text3.Length; i++)
					{
						if (!char.IsDigit(text3[i]))
						{
							flag = false;
							break;
						}
					}
					if (!flag)
					{
						return IsPlayerFacingField(text3);
					}
				}
			}
			return false;
		}

		private static bool HasLetterChar(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			for (int i = 0; i < text.Length; i++)
			{
				if (char.IsLetter(text[i]))
				{
					return true;
				}
			}
			return false;
		}

		private static bool IsPlayerFacingField(string field)
		{
			string text = (field ?? "").Trim().ToLowerInvariant();
			if (text.Length == 0)
			{
				return false;
			}
			switch (text)
			{
			case "label":
			case "labelshort":
			case "labelplural":
			case "description":
			case "text":
			case "message":
			case "reportstring":
			case "jobstring":
			case "gerund":
			case "verb":
			case "title":
			case "titleshort":
			case "inspectstring":
			case "customlabel":
			case "letterlabel":
			case "lettertext":
			case "headertip":
			case "labelnoun":
			case "descriptionshort":
			case "helptext":
			case "tooltip":
			case "tip":
			case "desc":
			case "commandlabel":
			case "commanddesc":
			case "gizmolabel":
			case "flavortext":
			case "flavourtext":
			case "deathmessage":
			case "extratooltip":
				return true;
			default:
				return text.Contains("label") || text.Contains("desc") || text.Contains("letter") || text.Contains("title") || text.Contains("tooltip") || text.Contains("jobstring");
			}
		}

		private static int SourceRankFor(string language, bool languagePack, List<string> allowed)
		{
			int num = -1;
			if (allowed != null)
			{
				for (int i = 0; i < allowed.Count; i++)
				{
					if (string.Equals(language, allowed[i], StringComparison.OrdinalIgnoreCase))
					{
						num = i;
						break;
					}
				}
			}
			if (languagePack)
			{
				return num;
			}
			if (num >= 0)
			{
				return num;
			}
			return 1000;
		}

		private static bool IsLanguagePackFile(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			string text = path.Replace('\\', '/');
			return text.IndexOf("/Languages/", StringComparison.OrdinalIgnoreCase) >= 0 || text.StartsWith("Languages/", StringComparison.OrdinalIgnoreCase);
		}

		private static string SourceLanguageOf(TranslationEntry source)
		{
			if (source == null)
			{
				return "English";
			}
			string normalizedLanguage = source.NormalizedLanguage;
			if (string.IsNullOrEmpty(normalizedLanguage))
			{
				return "English";
			}
			string text = LanguageCodeMapper.Canonical(normalizedLanguage);
			return string.IsNullOrEmpty(text) ? normalizedLanguage : text;
		}

		private static bool SameLooseText(string a, string b)
		{
			string text = (a ?? "").Trim();
			string b2 = (b ?? "").Trim();
			return text.Length > 0 && string.Equals(text, b2, StringComparison.OrdinalIgnoreCase);
		}
	}
	public sealed class TranslationCandidate
	{
		private sealed class StableId
		{
			public readonly string Target;

			public readonly string Identity;

			public StableId(string target, string identity)
			{
				Target = target;
				Identity = identity;
			}
		}

		private volatile StableId stable;

		public string ModName;

		public string ModPackageId;

		public string Type;

		public string DefType;

		public string Key;

		public string SourceLanguage;

		public string SourceText;

		public string FilePath;

		public string StableIdentityForTarget(string target)
		{
			string text = LanguageCodeMapper.Canonical(target);
			StableId stableId = stable;
			if (stableId != null && string.Equals(stableId.Target, text, StringComparison.OrdinalIgnoreCase))
			{
				return stableId.Identity;
			}
			string text2 = ModPackageId + "\n" + Type + "\n" + DefType + "\n" + Key + "\n" + text;
			stable = new StableId(text, text2);
			return text2;
		}
	}
	public sealed class TranslationInventory
	{
		public int TotalKeys;

		public int AlreadyTarget;

		public int MissingTarget;

		public List<TranslationCandidate> Candidates = new List<TranslationCandidate>();
	}
	public sealed class TranslationEntry
	{
		public string ModName;

		public string ModPackageId;

		public string Language;

		public string NormalizedLanguage;

		public string Key;

		public string Text;

		public string FilePath;

		public string Type;

		public string DefType;
	}
	public static class TranslationExporter
	{
		public static string ExportAll(TranslationCache cache, string targetLanguage)
		{
			if (cache == null)
			{
				throw new ArgumentNullException("cache");
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			if (string.IsNullOrWhiteSpace(target))
			{
				target = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			}
			string text = Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "Export", target);
			if (Directory.Exists(text))
			{
				Directory.Delete(text, recursive: true);
			}
			Directory.CreateDirectory(text);
			List<CachedTranslation> source = (from x in cache.GetAll()
				where x != null && string.Equals(LanguageCodeMapper.Canonical(x.TargetLanguage), target, StringComparison.OrdinalIgnoreCase)
				where !TranslationCache.IsLiveEntry(x)
				where TranslationCache.IsUsableTranslation(x, x.SourceText)
				select x).ToList();
			IEnumerable<IGrouping<string, CachedTranslation>> enumerable = source.GroupBy((CachedTranslation x) => string.IsNullOrWhiteSpace(x.ModPackageId) ? "UnknownMod" : x.ModPackageId, StringComparer.OrdinalIgnoreCase);
			int num = 0;
			int num2 = 0;
			foreach (IGrouping<string, CachedTranslation> item in enumerable)
			{
				string text2 = Path.Combine(text, Safe(item.Key));
				string text3 = Path.Combine(text2, "Languages", target);
				Directory.CreateDirectory(text3);
				WriteAbout(text2, item.Key, item.Select((CachedTranslation x) => x.ModName).FirstOrDefault((string x) => !string.IsNullOrWhiteSpace(x)), target);
				List<CachedTranslation> list = item.Where((CachedTranslation x) => string.Equals(x.Type, "Keyed", StringComparison.OrdinalIgnoreCase)).ToList();
				if (list.Count > 0)
				{
					string path = Path.Combine(text3, "Keyed.xml");
					WriteKeyed(path, list);
					num++;
					num2 += list.Count;
				}
				List<CachedTranslation> source2 = item.Where((CachedTranslation x) => string.Equals(x.Type, "DefInjected", StringComparison.OrdinalIgnoreCase)).ToList();
				foreach (IGrouping<string, CachedTranslation> item2 in source2.GroupBy((CachedTranslation x) => string.IsNullOrWhiteSpace(x.DefType) ? "UnknownDefType" : x.DefType, StringComparer.OrdinalIgnoreCase))
				{
					string text4 = Path.Combine(text3, "DefInjected", Safe(item2.Key));
					Directory.CreateDirectory(text4);
					string path2 = Path.Combine(text4, "UniversalRimTranslator.xml");
					WriteDefInjected(path2, item2);
					num++;
					num2 += item2.Count();
				}
			}
			return "Export complete: " + num2 + " translations in " + num + " XML files. Each folder is a standalone language mod. Output: " + text;
		}

		private static void WriteAbout(string modDir, string sourcePackageId, string modName, string target)
		{
			string text = Path.Combine(modDir, "About");
			Directory.CreateDirectory(text);
			string content = "URT " + target + " - " + (string.IsNullOrWhiteSpace(modName) ? sourcePackageId : modName);
			string content2 = "urt.generated." + UrtHash.Of(sourcePackageId ?? modName ?? "unknown");
			XElement xElement = new XElement("ModMetaData", new XElement("name", content), new XElement("author", "Universal RimTranslator"), new XElement("packageId", content2), new XElement("description", "Generated local translation pack. It does not change the original mod."), new XElement("supportedVersions", new XElement("li", VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor)));
			if (!string.IsNullOrWhiteSpace(sourcePackageId) && !string.Equals(sourcePackageId, "UnknownMod", StringComparison.OrdinalIgnoreCase))
			{
				xElement.Add(new XElement("loadAfter", new XElement("li", sourcePackageId)));
			}
			new XDocument(xElement).Save(Path.Combine(text, "About.xml"));
		}

		private static void WriteKeyed(string path, IEnumerable<CachedTranslation> entries)
		{
			XElement xElement = new XElement("LanguageData");
			foreach (CachedTranslation item in entries.OrderBy((CachedTranslation e) => e.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrWhiteSpace(item.Key))
				{
					string text = ((!string.IsNullOrWhiteSpace(item.Translation)) ? item.Translation : item.AutomaticTranslation);
					if (!string.IsNullOrWhiteSpace(text) && IsValidXmlName(item.Key))
					{
						xElement.Add(new XElement(item.Key, text));
					}
				}
			}
			Save(path, xElement);
		}

		private static void WriteDefInjected(string path, IEnumerable<CachedTranslation> entries)
		{
			XElement xElement = new XElement("LanguageData");
			foreach (CachedTranslation item in entries.OrderBy((CachedTranslation e) => e.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrWhiteSpace(item.Key))
				{
					string text = ((!string.IsNullOrWhiteSpace(item.Translation)) ? item.Translation : item.AutomaticTranslation);
					if (!string.IsNullOrWhiteSpace(text) && IsValidXmlName(item.Key))
					{
						xElement.Add(new XElement(item.Key, text));
					}
				}
			}
			Save(path, xElement);
		}

		private static void Save(string path, XElement root)
		{
			root.Save(path, SaveOptions.None);
		}

		private static string Safe(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "Unknown";
			}
			char[] invalid = Path.GetInvalidFileNameChars();
			char[] value2 = value.Select((char c) => invalid.Contains(c) ? '_' : c).ToArray();
			return new string(value2);
		}

		private static bool IsValidXmlName(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return false;
			}
			try
			{
				XmlConvert.VerifyName(value);
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
	public sealed class TranslationProviderResult
	{
		public bool Success;

		public List<string> Translations = new List<string>();

		public string Error;

		public int StatusCode;

		public int Attempts;

		public int CharacterCount;

		public List<string> RawResponses = new List<string>();
	}
	public interface IUniversalTranslationProvider
	{
		string Name { get; }

		Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken);
	}
	public sealed class TranslationProviderSettings
	{
		public string DeepLApiKey = "";

		public string GoogleCloudApiKey = "";

		public string MyMemoryEndpoint = "https://api.mymemory.translated.net/get";

		public string MyMemoryEmail = "";

		public string SimplyTranslateEndpoint = "https://api.simplytranslate.ai/translate";

		public string YandexEndpoint = "https://browser.translate.yandex.net/api/v1/tr.json/translate";

		public string LingvaEndpoint = "https://lingva.ml";

		public string GeminiApiKey = "";

		public string AzureApiKey = "";

		public string AzureRegion = "global";

		public string AzureEndpoint = "";

		public bool EnableDeepL = false;

		public bool EnableLingva = false;

		public bool EnableGemini = false;

		public bool EnableAzure = true;

		public bool EnableGoogleCloud = true;

		public bool EnableMyMemory = true;

		public bool EnableYandex = true;

		public bool EnableGoogleWeb = false;

		public bool EnableSimplyTranslate = false;

		public bool EnableMicrosoftEdge = true;

		public bool EnableCustomEndpoint = false;

		public string CustomEndpointType = "LibreTranslate";

		public string CustomEndpointUrl = "";

		public string CustomEndpointModel = "";

		public string CustomEndpointKey = "";
	}
	internal static class TranslationReviewExchange
	{
		private static readonly string[] Header = new string[10] { "ModName", "ModPackageId", "Type", "DefType", "Key", "SourceLanguage", "SourceText", "Translation", "TargetLanguage", "FilePath" };

		public static string ReviewFolder(string targetLanguage)
		{
			string text = LanguageCodeMapper.Canonical(targetLanguage);
			if (string.IsNullOrWhiteSpace(text))
			{
				text = UniversalRimTranslatorSettings.PlayerTargetLanguage();
			}
			return Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "Review", text);
		}

		public static string ExportUnresolved(IEnumerable<TranslationCandidate> candidates, string targetLanguage)
		{
			string text = LanguageCodeMapper.Canonical(targetLanguage);
			string text2 = ReviewFolder(text);
			Directory.CreateDirectory(text2);
			string text3 = Path.Combine(text2, "URT_unresolved.csv");
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("sep=,");
			WriteRow(stringBuilder, Header);
			int num = 0;
			foreach (TranslationCandidate item2 in candidates ?? Enumerable.Empty<TranslationCandidate>())
			{
				if (item2 != null && !string.IsNullOrWhiteSpace(item2.SourceText))
				{
					string item = item2.StableIdentityForTarget(text);
					if (hashSet.Add(item))
					{
						WriteRow(stringBuilder, new string[10]
						{
							item2.ModName ?? "",
							item2.ModPackageId ?? "",
							item2.Type ?? "",
							item2.DefType ?? "",
							item2.Key ?? "",
							item2.SourceLanguage ?? "",
							item2.SourceText ?? "",
							"",
							text,
							item2.FilePath ?? ""
						});
						num++;
					}
				}
			}
			File.WriteAllText(text3, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
			return "Review CSV exported: " + num + " texts. Add translations in column Translation, then press Import reviewed CSV. Path: " + text3;
		}

		public static ReviewImportResult ImportReviewed(TranslationCache cache, string targetLanguage)
		{
			ReviewImportResult reviewImportResult = new ReviewImportResult();
			if (cache == null)
			{
				reviewImportResult.Error = "Translation cache is not ready.";
				return reviewImportResult;
			}
			string text = LanguageCodeMapper.Canonical(targetLanguage);
			string path = ReviewFolder(text);
			string text2 = Path.Combine(path, "URT_reviewed.csv");
			string text3 = Path.Combine(path, "URT_unresolved.csv");
			string path2 = (File.Exists(text2) ? text2 : text3);
			if (!File.Exists(path2))
			{
				reviewImportResult.Error = "No review CSV found. Export unresolved texts first.";
				return reviewImportResult;
			}
			List<string[]> list;
			try
			{
				list = ParseCsv(File.ReadAllText(path2));
			}
			catch (Exception ex)
			{
				reviewImportResult.Error = "Review CSV could not be read: " + ex.Message;
				return reviewImportResult;
			}
			if (list.Count < 2)
			{
				reviewImportResult.Error = "Review CSV has no rows.";
				return reviewImportResult;
			}
			Dictionary<string, int> dictionary = HeaderMap(list[0]);
			if (!dictionary.TryGetValue("SourceText", out var value) || !dictionary.TryGetValue("Translation", out var value2))
			{
				reviewImportResult.Error = "Review CSV needs SourceText and Translation columns.";
				return reviewImportResult;
			}
			for (int i = 1; i < list.Count; i++)
			{
				string[] row = list[i];
				string text4 = Value(row, value);
				string text5 = Value(row, value2);
				if (string.IsNullOrWhiteSpace(text4) || string.IsNullOrWhiteSpace(text5))
				{
					reviewImportResult.Empty++;
					continue;
				}
				if (!UrtResultValidator.Accept(text4, text5))
				{
					reviewImportResult.Rejected++;
					continue;
				}
				string text6 = Value(row, dictionary, "TargetLanguage");
				if (!string.IsNullOrWhiteSpace(text6) && !string.Equals(LanguageCodeMapper.Canonical(text6), text, StringComparison.OrdinalIgnoreCase))
				{
					reviewImportResult.WrongLanguage++;
					continue;
				}
				CachedTranslation cachedTranslation = new CachedTranslation
				{
					ModName = Value(row, dictionary, "ModName"),
					ModPackageId = Value(row, dictionary, "ModPackageId"),
					Type = Value(row, dictionary, "Type"),
					DefType = Value(row, dictionary, "DefType"),
					Key = Value(row, dictionary, "Key"),
					SourceLanguage = Value(row, dictionary, "SourceLanguage"),
					SourceText = text4,
					OriginalSourceText = text4,
					TargetLanguage = text,
					Translation = text5,
					AutomaticTranslation = "",
					FilePath = Value(row, dictionary, "FilePath")
				};
				if (cache.Put(cachedTranslation))
				{
					reviewImportResult.ImportedEntries.Add(cachedTranslation);
				}
				else
				{
					reviewImportResult.Rejected++;
				}
			}
			reviewImportResult.Path = path2;
			return reviewImportResult;
		}

		private static void WriteRow(StringBuilder output, IEnumerable<string> values)
		{
			bool flag = true;
			foreach (string value in values)
			{
				if (!flag)
				{
					output.Append(',');
				}
				flag = false;
				string text = value ?? "";
				if (text.Length > 0 && (text[0] == '=' || text[0] == '+' || text[0] == '-' || text[0] == '@'))
				{
					text = "'" + text;
				}
				output.Append('"');
				output.Append(text.Replace("\"", "\"\""));
				output.Append('"');
			}
			output.AppendLine();
		}

		private static string Unprotect(string v)
		{
			if (v != null && v.Length > 1 && v[0] == '\'' && (v[1] == '=' || v[1] == '+' || v[1] == '-' || v[1] == '@'))
			{
				return v.Substring(1);
			}
			return v ?? "";
		}

		private static Dictionary<string, int> HeaderMap(string[] header)
		{
			Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < header.Length; i++)
			{
				string text = (header[i] ?? "").Trim().Trim(new char[1] { '\ufeff' });
				if (!string.IsNullOrWhiteSpace(text) && !dictionary.ContainsKey(text))
				{
					dictionary[text] = i;
				}
			}
			return dictionary;
		}

		private static string Value(string[] row, int index)
		{
			return (row != null && index >= 0 && index < row.Length) ? Unprotect(row[index]) : "";
		}

		private static string Value(string[] row, Dictionary<string, int> columns, string name)
		{
			int value;
			return columns.TryGetValue(name, out value) ? Value(row, value) : "";
		}

		private static List<string[]> ParseCsv(string value)
		{
			value = value ?? "";
			char c = ',';
			string text = value.TrimStart(new char[1] { '\ufeff' });
			if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
			{
				int num = text.IndexOf('\n');
				if (text.Length > 4 && text[4] != '\r' && text[4] != '\n')
				{
					c = text[4];
				}
				value = ((num < 0) ? "" : text.Substring(num + 1));
			}
			else
			{
				int num2 = text.IndexOf('\n');
				string text2 = ((num2 < 0) ? text : text.Substring(0, num2));
				int num3 = text2.Split(new char[1] { ',' }).Length;
				int num4 = text2.Split(new char[1] { ';' }).Length;
				if (num4 > num3)
				{
					c = ';';
				}
				value = text;
			}
			List<string[]> list = new List<string[]>();
			List<string> list2 = new List<string>();
			StringBuilder stringBuilder = new StringBuilder();
			bool flag = false;
			for (int i = 0; i < (value ?? "").Length; i++)
			{
				char c2 = value[i];
				if (flag)
				{
					if (c2 == '"' && i + 1 < value.Length && value[i + 1] == '"')
					{
						stringBuilder.Append('"');
						i++;
					}
					else if (c2 == '"')
					{
						flag = false;
					}
					else
					{
						stringBuilder.Append(c2);
					}
				}
				else if (c2 == '"')
				{
					flag = true;
				}
				else if (c2 == c)
				{
					list2.Add(stringBuilder.ToString());
					stringBuilder.Length = 0;
				}
				else if (c2 == '\r' || c2 == '\n')
				{
					if (c2 == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
					{
						i++;
					}
					list2.Add(stringBuilder.ToString());
					stringBuilder.Length = 0;
					if (list2.Count > 1 || list2[0].Length > 0)
					{
						list.Add(list2.ToArray());
					}
					list2.Clear();
				}
				else
				{
					stringBuilder.Append(c2);
				}
			}
			if (flag)
			{
				throw new FormatException("an opened quote is missing its closing quote");
			}
			if (list2.Count > 0 || stringBuilder.Length > 0)
			{
				list2.Add(stringBuilder.ToString());
				list.Add(list2.ToArray());
			}
			return list;
		}
	}
	internal sealed class ReviewImportResult
	{
		public readonly List<CachedTranslation> ImportedEntries = new List<CachedTranslation>();

		public int Empty;

		public int Rejected;

		public int WrongLanguage;

		public string Error = "";

		public string Path = "";

		public bool Success => string.IsNullOrWhiteSpace(Error);
	}
	public sealed class UniversalRimTranslatorMod : Mod
	{
		internal readonly UniversalRimTranslatorSettings settings;

		public UniversalRimTranslatorMod(ModContentPack content)
			: base(content)
		{
			UrtNet.Configure();
			// b113: static Regex calls are re-parsed when Mono's default cache (15) overflows
			try
			{
				if (Regex.CacheSize < 128)
				{
					Regex.CacheSize = 128;
				}
			}
			catch
			{
			}
			LongEventHandler.ExecuteWhenFinished(UrtFrameWatch.Create);
			settings = GetSettings<UniversalRimTranslatorSettings>();
			int num = UrtCzechXml.RemoveShippedFiles((content != null) ? content.RootDir : "");
			if (num > 0)
			{
				MLog.Info("[Universal RimTranslator] Removed " + num + " generated language files that were overriding the language pack.");
			}
			MLog.Game("[Universal RimTranslator] v" + UrtVersion.Display + " loaded (messages: Logs\\URT.log).");
		}

		public override string SettingsCategory()
		{
			return "Universal RimTranslator";
		}

		public override void DoSettingsWindowContents(Rect inRect)
		{
			RuntimeTextTranslator.OwnWindowDepth++;
			try
			{
				settings.DoWindowContents(inRect);
			}
			finally
			{
				RuntimeTextTranslator.OwnWindowDepth--;
			}
		}
	}
	[StaticConstructorOnStartup]
	internal static class StartupTranslationApply
	{
		static StartupTranslationApply()
		{
			LongEventHandler.ExecuteWhenFinished(Apply);
		}

		private static void Apply()
		{
			UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
			if (mod == null || mod.settings == null)
			{
				MLog.Info("[Universal RimTranslator] startup apply skipped (no settings).");
				return;
			}
			mod.settings.WarmRuntimeDictionary();
			MLog.Info("[URT-LIVE] Startup bootstrap armed; waiting for Playing state.");
		}
	}
	internal sealed class UrtLiveStartupComponent : GameComponent
	{
		private bool armed;

		public UrtLiveStartupComponent(Game game)
		{
		}

		public override void GameComponentUpdate()
		{
			if (!armed && Current.ProgramState == ProgramState.Playing)
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod != null && mod.settings != null)
				{
					armed = true;
					mod.settings.EnableLiveFromStartup();
				}
			}
		}
	}
	public sealed class UniversalRimTranslatorSettings : ModSettings
	{
		private static class UrtManual
		{
			private static readonly object Gate = new object();

			private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			public static int Count
			{
				get
				{
					lock (Gate)
					{
						return Map.Count;
					}
				}
			}

			private static string FilePath()
			{
				return Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "ManualOverrides.txt");
			}

			private static string Norm(string s)
			{
				return (s ?? "").Trim();
			}

			public static bool Has(string source)
			{
				string text = Norm(source);
				if (text.Length == 0)
				{
					return false;
				}
				lock (Gate)
				{
					return Map.ContainsKey(text);
				}
			}

			public static void Put(string source, string translation)
			{
				string text = Norm(source);
				string text2 = Norm(translation);
				if (text.Length != 0 && text2.Length != 0)
				{
					lock (Gate)
					{
						Map[text] = text2;
					}
					Save();
				}
			}

			public static void Load()
			{
				Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				try
				{
					string path = FilePath();
					if (!File.Exists(path))
					{
						lock (Gate)
						{
							Map.Clear();
							return;
						}
					}
					string[] array = File.ReadAllLines(path, Encoding.UTF8);
					foreach (string text in array)
					{
						if (string.IsNullOrWhiteSpace(text) || text.StartsWith("#"))
						{
							continue;
						}
						int num = text.IndexOf('\t');
						if (num > 0)
						{
							string text2 = UnescapeLine(text.Substring(0, num));
							string text3 = UnescapeLine(text.Substring(num + 1));
							if (text2.Length > 0 && text3.Length > 0)
							{
								dictionary[text2] = text3;
							}
						}
					}
				}
				catch
				{
				}
				lock (Gate)
				{
					Map.Clear();
					foreach (KeyValuePair<string, string> item in dictionary)
					{
						Map[item.Key] = item.Value;
					}
				}
			}

			// b114: backslash is escaped too, so RimWorld's literal "\n" survives a save/load round trip
			// (old files: a stored "\n" still loads as a newline, exactly as before)
			private static string EscapeLine(string text)
			{
				return (text ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");
			}

			private static string UnescapeLine(string text)
			{
				if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0)
				{
					return text ?? "";
				}
				StringBuilder stringBuilder = new StringBuilder(text.Length);
				for (int i = 0; i < text.Length; i++)
				{
					char c = text[i];
					if (c == '\\' && i + 1 < text.Length)
					{
						char c2 = text[i + 1];
						if (c2 == '\\')
						{
							stringBuilder.Append('\\');
							i++;
							continue;
						}
						if (c2 == 'n')
						{
							stringBuilder.Append('\n');
							i++;
							continue;
						}
						if (c2 == 't')
						{
							stringBuilder.Append('\t');
							i++;
							continue;
						}
					}
					stringBuilder.Append(c);
				}
				return stringBuilder.ToString();
			}

			private static readonly object FileGate = new object();

			public static void Save()
			{
				try
				{
					string path = FilePath();
					List<string> list = new List<string>();
					lock (Gate)
					{
						foreach (KeyValuePair<string, string> item in Map)
						{
							list.Add(EscapeLine(item.Key) + "\t" + EscapeLine(item.Value));
						}
					}
					// b114: write to .tmp and swap, one writer at a time (a crash mid-write no longer wipes the overrides)
					lock (FileGate)
					{
						Directory.CreateDirectory(Path.GetDirectoryName(path));
						string text = path + ".tmp";
						File.WriteAllLines(text, list.ToArray(), Encoding.UTF8);
						if (File.Exists(path))
						{
							File.Replace(text, path, null);
						}
						else
						{
							File.Move(text, path);
						}
					}
				}
				catch (Exception ex)
				{
					MLog.Warn("[URT] Manual overrides save failed: " + ex.GetType().Name + " " + ex.Message);
				}
			}

			public static void MergeInto(TranslationCache cache, string targetLang)
			{
				if (cache == null)
				{
					return;
				}
				List<KeyValuePair<string, string>> list;
				lock (Gate)
				{
					list = new List<KeyValuePair<string, string>>(Map);
				}
				foreach (KeyValuePair<string, string> item in list)
				{
					cache.Put(new CachedTranslation
					{
						ModName = "Manual",
						ModPackageId = "urt.manual",
						Type = "Keyed",
						DefType = "Manual",
						Key = "manual." + (uint)item.Key.GetHashCode(),
						SourceLanguage = "Auto",
						SourceText = item.Key,
						OriginalSourceText = item.Key,
						TargetLanguage = (string.IsNullOrWhiteSpace(targetLang) ? "English" : targetLang),
						Translation = item.Value,
						AutomaticTranslation = item.Value
					});
					RuntimeTextTranslator.RegisterLive(item.Key, item.Value);
				}
			}
		}

		public int ModsScanned;

		public int ModsWithLanguages;

		public int ModsWithTranslatableText;

		public int LanguageFolders;

		public int FilesScanned;

		public int EntriesExtracted;

		public List<string> Languages = new List<string>();

		private readonly TranslationDatabase database = new TranslationDatabase();

		private readonly TranslationCache cache = new TranslationCache();

		private readonly DeepLProvider provider = new DeepLProvider();

		private readonly GoogleTranslateProvider fallbackProvider = new GoogleTranslateProvider();

		private readonly MyMemoryTranslateProvider myMemoryProvider = new MyMemoryTranslateProvider();

		private readonly SimplyTranslateAiProvider simplyTranslateProvider = new SimplyTranslateAiProvider();

		private readonly YandexTranslateProvider yandexProvider = new YandexTranslateProvider();

		private readonly TranslationBatchManager batchManager = new TranslationBatchManager();

		private TranslationInventory inventory = new TranslationInventory();

		private string deepLApiKey = "";

		private string googleApiKey = "";

		private string myMemoryEndpoint = "https://api.mymemory.translated.net/get";

		private string myMemoryEmail = "";

		private string simplyTranslateEndpoint = "https://api.simplytranslate.ai/translate";

		private string yandexEndpoint = "https://browser.translate.yandex.net/api/v1/tr.json/translate";

		private bool enableDeepL = false;

		private bool enableGoogleCloud = true;

		private bool enableMyMemory = true;

		private bool enableSimplyTranslate = false;

		private bool enableYandex = true;

		private bool enableGoogleWeb = false;

		private bool enableLingva = false;

		private bool enableGemini = false;

		private string geminiApiKey = "";

		private bool enableAzure = true;

		private bool enableLiveTranslator = true;

		private bool useLiveV2 = true;

		public static bool WriteDefsAtStart = true;

		private bool writeDefsAtStart = true;

		private bool liveLockedOff;

		private string azureApiKey = "";

		private string azureRegion = "global";

		private string azureEndpoint = "";

		private bool enableMicrosoftEdge = true;

		private bool enableCustomEndpoint = false;

		private string customEndpointType = "LibreTranslate";

		private string customEndpointUrl = "";

		private string customEndpointModel = "";

		private string customEndpointKey = "";

		public bool wideCoverage = false;

		private Vector2 providerScrollPosition = Vector2.zero;

		private string targetLanguage = "";

		private bool targetLanguageChosen = false;

		private bool sourceLanguagesChosen = false;

		private bool autoMainLanguage = true;

		private Dictionary<string, string> mainLanguageByMod;

		private static readonly Dictionary<string, UniversalRimTranslatorUiText> UiTextMap = UniversalRimTranslatorUiCatalog.BuildMap();

		private List<string> selectedSourceLanguages = new List<string> { "English" };

		private string status = "Not tested.";

		private string batchAmountText = "5000";

		private CancellationTokenSource autoCancellation;

		private bool autoRunning;

		private int autoCandidateIndex;

		private int autoBatchesSinceSave;

		private bool autoCursorWrapped;

		private bool legacyFailedMigrationAttempted;

		private List<TranslationCandidate> autoStableQueue;

		private int autoProviderWaits;

		private int autoStablePos;

		private bool googleTestRunning;

		private bool deepLTestRunning;

		private bool providerTestRunning;

		private readonly Dictionary<string, string> providerTestStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private int activeTab;

		private string selectedMod = "(All mods)";

		private string editorSearch = "";

		private string editorDraft = "";

		private string editorSelectedId = "";

		private CachedTranslation editorSelectedEntry;

		private List<CachedTranslation> editorEntriesCache = new List<CachedTranslation>();

		private string editorEntriesCacheKey = null;

		private bool editorListBuildInProgress;

		private Dictionary<string, CachedTranslation> editorBuildMap;

		private IEnumerator<CachedTranslation> editorBuildCacheEnumerator;

		private IEnumerator<TranslationCandidate> editorBuildCandidateEnumerator;

		private HashSet<string> editorBuildSelectedPackageIds;

		private string editorBuildKey;

		private const int EditorBuildItemsPerFrame = 500;

		private readonly ConcurrentDictionary<string, string> autoRunFailedCandidates = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private readonly ConcurrentDictionary<string, int> autoRunRetryCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		private const int AutoMaxAttemptsPerCandidate = 3;

		private bool cacheLoaded;

		private bool inventoryReady;

		private Task<TranslationInventory> inventoryBuildTask;

		private TranslationDatabase.IncrementalInventoryBuilder inventoryIncremental;

		private string inventoryBuildTargetLanguage;

		private const int InventoryItemsPerFrame = 250;

		private Vector2 editorScroll;

		private Vector2 editorSourceScroll;

		private Vector2 editorCurrentScroll;

		private Vector2 editorDraftScroll;

		private Vector2 sourceLangScroll;

		private bool sourcePickerOpen;

		private bool modTextSearchRunning;

		private string modTextSearchResult = "";

		private bool deepEditorSearchRunning;

		private string deepEditorSearchKey = null;

		private List<CachedTranslation> deepEditorEntries = new List<CachedTranslation>();

		private static readonly Color HudPanel = new Color(0.075f, 0.085f, 0.095f, 0.98f);

		private static readonly Color HudPanelAlt = new Color(0.105f, 0.115f, 0.13f, 0.98f);

		private static readonly Color HudAccent = new Color(0.82f, 0.62f, 0.28f, 1f);

		private static readonly Color HudAccentSoft = new Color(0.48f, 0.36f, 0.2f, 1f);

		private static readonly Color HudGreen = new Color(0.35f, 0.68f, 0.43f, 1f);

		private static readonly Color HudBlue = new Color(0.3f, 0.55f, 0.78f, 1f);

		private static readonly Color HudRed = new Color(0.72f, 0.3f, 0.28f, 1f);

		private static readonly Color HudMuted = new Color(0.6f, 0.63f, 0.66f, 1f);

		private string modSearch = "";

		private Vector2 modScroll;

		private const float UiRefreshInterval = 1f / 30f;

		private float nextUiRefreshTime;

		private List<string> cachedModNames = new List<string>();

		private bool cachedModNamesReady;

		private bool autoScanQueued;

		private int uiPending;

		private int uiCandidates;

		private int uiScanCandidates;

		private int uiCountStamp;

		public static int SettingsDrawFrame;

		private int pendingRecountRunning;

		private volatile bool uiPendingKnown;

		private static int pendingCountErrorsLogged;

		private bool scanRunning;

		private bool purgeDirtyRunning;

		private static readonly HashSet<string> HarvestTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"label", "description", "labelNoun", "labelPlural", "labelSocial", "customLabel", "jobString", "reportString", "gerund", "verb",
			"text", "title", "header", "tooltip", "letterLabel", "letterText", "deathMessage", "skillLabel", "pawnSingular", "pawnsPlural"
		};

		private bool startTranslateAfterScan;

		private int liveDirty;

		private readonly object liveFlushGate = new object();

		private readonly Stopwatch liveFlushClock = Stopwatch.StartNew();

		private long lastLiveFlushMs = -60000L;

		private static int runtimeRefreshRunning;

		private static volatile bool runtimeRefreshAgain;

		private int autoFailSweeps;

		private int autoAppliedFromSource;

		private string CachePath => Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslatorCache.xml");

		private string LiveCachePath => Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslatorCache.live.xml");

		private string AutoSkipFailedPath => Path.Combine(GenFilePaths.ConfigFolderPath, "URT_auto_skip_failed.txt");

		private string LiveSkipFailedPath => Path.Combine(GenFilePaths.ConfigFolderPath, "URT_skip_failed.txt");

		private string LegacySkipMigrationPath => Path.Combine(GenFilePaths.ConfigFolderPath, "URT_auto_skip_legacy_migrated.txt");

		private string[] Tabs => new string[5]
		{
			UiText("URT_UI_TAB_TRANSLATION"),
			UiText("URT_UI_TAB_MODS"),
			UiText("URT_UI_TAB_EDITOR"),
			UiText("URT_UI_TAB_PROVIDERS"),
			UiText("URT_UI_TAB_CACHE")
		};

		private string KnownModsPath => Path.Combine(GenFilePaths.ConfigFolderPath, "URT_known_mods.txt");

		internal void WarmRuntimeDictionary()
		{
			try
			{
				EnsureCacheLoaded();
				List<CachedTranslation> all = cache.GetAll();
				RuntimeTextTranslator.Refresh(all);
				LanguageInjection.Inject(cache);
				try
				{
					if (WriteDefsAtStart)
					{
						long ms;
						int num = DefTranslationApplier.ApplyDefsAtStartup(cache, out ms);
						MLog.Info("[URT] b105 translations written into Defs at start: " + num + " (" + ms + " ms; names, descriptions, grammar rules).");
						MLog.Info("[URT] b108 generated texts written into Defs at start: " + RuntimeTextTranslator.WriteGeneratedIntoDefs() + " (recipes, corpses, meat, blueprints, frames, techprints, psytrainers).");
					}
					else
					{
						MLog.Info("[URT] b107 writing into Defs at start is OFF; only grammar rules written: " + DefTranslationApplier.ApplyRulesAtStartup(cache) + ".");
					}
				}
				catch (Exception ex)
				{
					MLog.Warn("[URT] b99 grammar rules not applied: " + ex.GetType().Name + " " + ex.Message);
				}
				string packTarget = targetLanguage;
				ThreadPool.QueueUserWorkItem(delegate
				{
					try
					{
						UrtPackLibrary.Ensure(packTarget);
					}
					catch (Exception ex3)
					{
						MLog.Warn("[URT] Pack library load failed: " + ex3.Message);
					}
				});
				MLog.Info("[URT] Runtime dictionary warmed from cache: " + cache.Count + " entries, " + (all?.Count ?? 0) + " loaded. Defs are not rewritten at boot.");
			}
			catch (Exception ex2)
			{
				MLog.Warn("[URT] Runtime dictionary warm failed: " + ex2.GetType().Name + " " + ex2.Message);
			}
		}

		public static bool UseWideCoverage()
		{
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod == null || mod.settings == null)
				{
					return true;
				}
				return mod.settings.wideCoverage;
			}
			catch
			{
				return true;
			}
		}

		private void LoadSkipFailed()
		{
			try
			{
				if (!File.Exists(AutoSkipFailedPath))
				{
					return;
				}
				string[] array = File.ReadAllLines(AutoSkipFailedPath);
				foreach (string text in array)
				{
					try
					{
						if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("v2:", StringComparison.Ordinal))
						{
							string @string = Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim().Substring(3)));
							int num = @string.LastIndexOf('\n');
							if (num > 0 && num < @string.Length - 1)
							{
								autoRunFailedCandidates.TryAdd(@string.Substring(0, num), @string.Substring(num + 1));
							}
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
		}

		private static readonly object skipFileGate = new object();

		private void SaveSkipFailed()
		{
			try
			{
				string[] contents = autoRunFailedCandidates.Select((KeyValuePair<string, string> pair) => "v2:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key + "\n" + pair.Value))).ToArray();
				// b114: SaveCache runs from Task.Run / ThreadPool too; serialize the write
				lock (skipFileGate)
				{
					File.WriteAllLines(AutoSkipFailedPath, contents);
				}
			}
			catch
			{
			}
		}

		internal void EnableLiveFromStartup()
		{
			try
			{
				if (Current.ProgramState == ProgramState.Playing)
				{
					if (!enableLiveTranslator)
					{
						RuntimeLiveQueue.SetUserEnabled(on: false);
						MLog.Info("[Universal RimTranslator] Startup: Live translator stays OFF (user setting).");
						return;
					}
					liveLockedOff = false;
					RuntimeLiveQueue.Resume();
					RuntimeLiveQueue.SetUserEnabled(on: true);
					MLog.Info("[Universal RimTranslator] Startup: Live translator ON.");
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Universal RimTranslator] Startup Live enable failed: " + ex.GetType().Name + " " + ex.Message);
			}
		}

		public void DoWindowContents(Rect rect)
		{
			SettingsDrawFrame = Time.frameCount;
			EnsureCacheLoaded();
			if (liveLockedOff || scanRunning)
			{
				RuntimeLiveQueue.Pause();
				RuntimeLiveQueue.SetUserEnabled(on: false);
			}
			else
			{
				RuntimeLiveQueue.SetUserEnabled(enableLiveTranslator);
			}
			PumpInventoryBuild();
			UrtMainThread.Pump();
			UrtApplyQueue.Pump();
			Rect rect2 = new Rect(0f, 0f, rect.width, 38f);
			DrawTabs(rect2);
			Rect rect3 = new Rect(0f, 46f, rect.width, Mathf.Max(0f, rect.height - 46f - 72f));
			switch (activeTab)
			{
			case 0:
				DrawTranslationTab(rect3);
				break;
			case 1:
				DrawModsTab(rect3);
				break;
			case 2:
				DrawEditorTab(rect3);
				break;
			case 3:
				DrawProvidersTab(rect3);
				break;
			case 4:
				DrawCacheTab(rect3);
				break;
			}
		}

		private bool ButtonLabelTiny(Rect rect, string label)
		{
			GameFont font = Text.Font;
			Text.Font = GameFont.Tiny;
			bool result = ButtonLabel(rect, label);
			Text.Font = font;
			return result;
		}

		private bool ButtonLabel(Rect rect, string label)
		{
			bool flag = Mouse.IsOver(rect);
			bool flag2 = flag && Event.current.type == EventType.MouseDown && Event.current.button == 0;
			if (flag2)
			{
				Event.current.Use();
			}
			Color color = (flag ? HudPanelAlt : HudPanel);
			Widgets.DrawBoxSolid(rect, color);
			Widgets.DrawBox(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3f, rect.height), flag ? HudAccent : HudAccentSoft);
			if (flag)
			{
				Widgets.DrawHighlightIfMouseover(rect);
			}
			Rect rect2 = new Rect(rect.x + 12f, rect.y + 2f, Mathf.Max(0f, rect.width - 18f), Mathf.Max(0f, rect.height - 4f));
			bool wordWrap = Text.WordWrap;
			Text.WordWrap = false;
			Widgets.Label(rect2, label ?? "");
			Text.WordWrap = wordWrap;
			return flag2;
		}

		private static bool ClickedOnce(Rect rect)
		{
			bool flag = Mouse.IsOver(rect) && Event.current.type == EventType.MouseDown && Event.current.button == 0;
			if (flag)
			{
				Event.current.Use();
			}
			return flag;
		}

		private bool ButtonLabel(Rect rect, string label, Color accent, bool strong)
		{
			bool result = ClickedOnce(rect);
			bool flag = Mouse.IsOver(rect);
			Color color = (strong ? new Color(0.11f, 0.115f, 0.12f, 1f) : HudPanel);
			if (flag)
			{
				color = new Color(0.17f, 0.175f, 0.18f, 1f);
			}
			Widgets.DrawBoxSolid(rect, color);
			Widgets.DrawBox(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 4f, rect.height), accent);
			if (flag)
			{
				Widgets.DrawHighlightIfMouseover(rect);
			}
			Rect rect2 = new Rect(rect.x + 13f, rect.y + 2f, Mathf.Max(0f, rect.width - 18f), Mathf.Max(0f, rect.height - 4f));
			bool wordWrap = Text.WordWrap;
			Text.WordWrap = false;
			Widgets.Label(rect2, label ?? "");
			Text.WordWrap = wordWrap;
			return result;
		}

		private bool Button(Listing_Standard listing, string label, float height = 32f)
		{
			Rect rect = listing.GetRect(height);
			bool result = ButtonLabel(rect, label);
			listing.Gap(4f);
			return result;
		}

		private bool TabButton(Rect rect, string label, bool active)
		{
			bool result = ClickedOnce(rect);
			bool flag = Mouse.IsOver(rect);
			Widgets.DrawBoxSolid(rect, active ? new Color(0.22f, 0.22f, 0.23f, 1f) : (flag ? HudPanelAlt : HudPanel));
			Widgets.DrawBox(rect);
			if (active)
			{
				Widgets.DrawBoxSolid(new Rect(rect.x, rect.y + rect.height - 3f, rect.width, 3f), HudAccent);
			}
			else
			{
				Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3f, rect.height), HudAccentSoft);
			}
			Rect rect2 = new Rect(rect.x + 12f, rect.y + 4f, Mathf.Max(0f, rect.width - 20f), Mathf.Max(0f, rect.height - 8f));
			Widgets.Label(rect2, label ?? "");
			return result;
		}

		private void Panel(Rect rect)
		{
			Widgets.DrawBoxSolid(rect, HudPanel);
			Widgets.DrawBox(rect);
		}

		private void SectionHeader(Rect rect, string title, string subtitle = null)
		{
			Panel(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 4f, rect.height), HudAccent);
			Widgets.Label(new Rect(rect.x + 14f, rect.y + 6f, rect.width - 20f, 24f), title);
			if (!string.IsNullOrEmpty(subtitle))
			{
				Widgets.Label(new Rect(rect.x + 14f, rect.y + 30f, rect.width - 20f, 22f), subtitle);
			}
		}

		private void DrawTabs(Rect rect)
		{
			float num = 5f;
			float num2 = (rect.width - num * (float)(Tabs.Length - 1)) / (float)Tabs.Length;
			for (int i = 0; i < Tabs.Length; i++)
			{
				Rect rect2 = new Rect(rect.x + (float)i * (num2 + num), rect.y, num2, 34f);
				if (TabButton(rect2, Tabs[i], activeTab == i))
				{
					if (activeTab != i)
					{
						sourcePickerOpen = false;
					}
					activeTab = i;
				}
			}
		}

		private void DrawTranslationTab(Rect rect)
		{
			if (sourcePickerOpen)
			{
				DrawSourcePicker(rect);
				return;
			}
			float num = 10f;
			float num2 = 116f;
			Rect rect2 = new Rect(rect.x, rect.y, rect.width, num2);
			Panel(rect2);
			Widgets.DrawBoxSolid(new Rect(rect2.x, rect2.y, 4f, rect2.height), autoRunning ? HudGreen : HudAccentSoft);
			float num3 = rect2.width * 0.66f;
			Widgets.Label(new Rect(rect2.x + 16f, rect2.y + 8f, 300f, 22f), UiText("URT_UI_CURRENT_RUN"));
			if (batchManager.Progress != null && batchManager.Progress.Finished && !batchManager.Progress.Running && batchManager.Progress.Failed > 0 && batchManager.LastFailedCandidates != null && batchManager.LastFailedCandidates.Count > 0)
			{
				Rect rect3 = new Rect(rect2.x + num3 - 235f, rect2.y + 7f, 220f, 30f);
				if (ButtonLabel(rect3, "↻  FIX FAILED", HudRed, strong: true))
				{
					RetryLastFailedCandidates();
				}
			}
			DrawProgressCompact(new Rect(rect2.x + 16f, rect2.y + 31f, num3 - 20f, rect2.height - 36f));
			float x = rect2.x + rect2.width * 0.69f;
			float width = rect2.width * 0.28f;
			Widgets.Label(new Rect(x, rect2.y + 8f, width, 20f), UiText("URT_UI_CREATOR"));
			Rect rect4 = new Rect(x, rect2.y + 32f, width, 30f);
			if (ButtonLabel(rect4, UiText("URT_UI_STEAM_PROFILE"), HudBlue, strong: true))
			{
				Application.OpenURL("https://steamcommunity.com/profiles/76561198126386329/");
			}
			float y = rect.y + num2 + num;
			float height = 268f;
			float num4 = Mathf.Floor(rect.width * 0.55f);
			float x2 = rect.x + num4 + num;
			float width2 = rect.width - num4 - num;
			Rect rect5 = new Rect(rect.x, y, num4, height);
			Panel(rect5);
			Widgets.DrawBoxSolid(new Rect(rect5.x, rect5.y, 4f, rect5.height), HudAccent);
			Widgets.Label(new Rect(rect5.x + 16f, rect5.y + 10f, rect5.width - 28f, 24f), UiText("URT_UI_TRANSLATION_SETTINGS"));
			Widgets.Label(new Rect(rect5.x + 16f, rect5.y + 34f, rect5.width - 28f, 20f), UiText("URT_UI_TRANSLATION_SETTINGS_DESC"));
			float num5 = rect5.y + 66f;
			float width3 = rect5.width - 28f;
			Rect rect6 = new Rect(rect5.x + 14f, num5, width3, 38f);
			if (ButtonLabel(rect6, UiText("URT_UI_TARGET_LANGUAGE") + "   •   " + targetLanguage + "   ▼", HudAccent, strong: true))
			{
				ShowTargetLanguageMenu(rect6);
			}
			num5 += 45f;
			Rect rect7 = new Rect(rect5.x + 14f, num5, width3, 38f);
			if (ButtonLabel(rect7, UiText("URT_UI_SOURCE_LANGUAGES") + "   •   " + FormatSelectedSourcesShort() + "   ▼", HudGreen, strong: false))
			{
				sourcePickerOpen = !sourcePickerOpen;
			}
			num5 += 45f;
			Rect rect8 = new Rect(rect5.x + 14f, num5, width3, 38f);
			string text = ((selectedMod == "(All mods)") ? UiText("URT_UI_ALL_WORKSHOP_MODS") : selectedMod);
			if (ButtonLabel(rect8, UiText("URT_UI_TRANSLATION_SCOPE") + "   •   " + text + "   →", HudBlue, strong: false))
			{
				activeTab = 1;
			}
			num5 += 47f;
			RefreshUiCountsIfNeeded();
			string text2 = "";
			if (autoRunning && batchManager.Progress != null)
			{
				text2 = "    |    " + (batchManager.Progress.Status ?? "working…");
			}
			else if (autoRunFailedCandidates.Count > 0)
			{
				text2 = "    |    retry later: " + autoRunFailedCandidates.Count;
			}
			Widgets.Label(new Rect(rect5.x + 16f, num5, rect5.width - 32f, 23f), UiText("URT_UI_PENDING") + "  " + (uiPendingKnown ? uiPending.ToString("N0") : "…") + "    |    " + UiText("URT_UI_CANDIDATES") + "  " + uiCandidates.ToString("N0") + "    |    " + UiText("URT_UI_CACHE_WORD") + "  " + cache.Count.ToString("N0") + text2);
			Rect rect9 = new Rect(rect5.x + 14f, num5 + 29f, width3, 34f);
			Widgets.DrawBoxSolid(rect9, new Color(0.105f, 0.115f, 0.125f, 1f));
			Color color = ((selectedMod == "(All mods)") ? HudBlue : HudGreen);
			Widgets.DrawBoxSolid(new Rect(rect9.x, rect9.y, 4f, rect9.height), color);
			Widgets.Label(new Rect(rect9.x + 12f, rect9.y + 3f, rect9.width - 18f, 28f), (selectedMod == "(All mods)") ? UiText("URT_UI_SCOPE_EVERY_MOD") : ("Scope: ONLY " + selectedMod));
			Rect rect10 = new Rect(x2, y, width2, height);
			Panel(rect10);
			Widgets.DrawBoxSolid(new Rect(rect10.x, rect10.y, 4f, rect10.height), HudBlue);
			Widgets.Label(new Rect(rect10.x + 16f, rect10.y + 10f, rect10.width - 28f, 24f), UiText("URT_UI_TRANSLATION_CONTROL"));
			Widgets.Label(new Rect(rect10.x + 16f, rect10.y + 34f, rect10.width - 28f, 20f), UiText("URT_UI_TRANSLATION_CONTROL_DESC"));
			float num6 = rect10.y + 66f;
			Rect rect11 = new Rect(rect10.x + 14f, num6, rect10.width - 28f, 42f);
			bool flag = inventory == null;
			string label = "⌕   " + UiText("URT_UI_SCAN_STEP1") + "   •   " + (autoMainLanguage ? UiText("URT_UI_SCAN_AUTO_LANG") : (UiText("URT_UI_SCAN_MANUAL_LANG") + ": " + FormatSelectedSourcesShort()));
			if (ButtonLabel(rect11, label, flag ? HudGreen : HudBlue, strong: true))
			{
				ScanFromMainButton();
			}
			num6 += 48f;
			Widgets.Label(new Rect(rect10.x + 16f, num6, rect10.width - 32f, 20f), UiText("URT_UI_TEXTS_PER_RUN"));
			batchAmountText = Widgets.TextField(new Rect(rect10.x + 14f, num6 + 22f, rect10.width - 28f, 31f), batchAmountText ?? "5000");
			num6 += 65f;
			if (!autoRunning && (batchManager.Progress == null || !batchManager.Progress.Running))
			{
				Rect rect12 = new Rect(rect10.x + 14f, num6, rect10.width - 28f, 36f);
				if (ButtonLabel(rect12, UiText("URT_UI_TRANSLATE_SELECTED_AMOUNT"), HudGreen, strong: false))
				{
					TranslateNextBatch();
				}
				num6 += 43f;
				Rect rect13 = new Rect(rect10.x + 14f, num6, rect10.width - 28f, 42f);
				if (ButtonLabel(rect13, "▶   " + UiText("URT_UI_TRANSLATE_ALL_MISSING"), HudAccent, strong: true))
				{
					StartAutomaticTranslation();
				}
			}
			else
			{
				Rect rect14 = new Rect(rect10.x + 14f, num6, rect10.width - 28f, 42f);
				if (ButtonLabel(rect14, "■   " + UiText("URT_UI_STOP_AUTOMATIC"), HudRed, strong: true))
				{
					StopAutomaticTranslation();
				}
			}
		}

		private void DrawSourcePicker(Rect host)
		{
			Rect rect = new Rect(host.x + 6f, host.y + 6f, host.width - 12f, host.height - 12f);
			Widgets.DrawBoxSolid(rect, new Color(0.07f, 0.075f, 0.082f, 0.98f));
			Widgets.DrawBox(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 4f, rect.height), HudGreen);
			Widgets.Label(new Rect(rect.x + 16f, rect.y + 10f, 220f, 24f), UiText("URT_UI_SOURCE_LANGUAGES"));
			Rect rect2 = new Rect(rect.xMax - 430f, rect.y + 8f, 90f, 28f);
			Rect rect3 = new Rect(rect.xMax - 332f, rect.y + 8f, 110f, 28f);
			Rect rect4 = new Rect(rect.xMax - 214f, rect.y + 8f, 90f, 28f);
			Rect rect5 = new Rect(rect.xMax - 116f, rect.y + 8f, 100f, 28f);
			if (ButtonLabel(rect2, "AUTO", HudGreen, strong: false))
			{
				ApplyAutoLanguagesFromMods(startTranslate: false);
			}
			if (ButtonLabel(rect3, UiText("URT_UI_SELECT_ALL"), HudGreen, strong: false))
			{
				selectedSourceLanguages = GetSourceLanguageOptions().ToList();
				RemoveTargetFromSources();
				sourceLanguagesChosen = true;
				SetAutoMain(on: false);
				PersistChoices();
				status = string.Format(UiText("URT_UI_SOURCES_SELECTED"), selectedSourceLanguages.Count);
			}
			if (ButtonLabel(rect4, UiText("URT_UI_CLEAR_ALL"), HudRed, strong: false))
			{
				selectedSourceLanguages.Clear();
				sourceLanguagesChosen = true;
				SetAutoMain(on: false);
				PersistChoices();
				status = UiText("URT_UI_SOURCES_CLEARED");
			}
			if (ButtonLabel(rect5, UiText("URT_UI_DONE"), HudAccent, strong: true))
			{
				sourcePickerOpen = false;
			}
			if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
			{
				sourcePickerOpen = false;
				Event.current.Use();
			}
			DrawSourceLanguageGrid(new Rect(rect.x + 12f, rect.y + 44f, rect.width - 24f, rect.height - 96f));
			Rect rect6 = new Rect(rect.x + 12f, rect.yMax - 44f, rect.width - 24f, 32f);
			if (ButtonLabel(rect6, UiText("URT_UI_DONE_CLOSE"), HudAccent, strong: true))
			{
				sourcePickerOpen = false;
			}
		}

		private void ShowTargetLanguageMenu(Rect anchor)
		{
			List<string> targetLanguageOptions = GetTargetLanguageOptions();
			List<FloatMenuOption> list = new List<FloatMenuOption>();
			foreach (string item in targetLanguageOptions)
			{
				string captured = item;
				bool flag = string.Equals(captured, targetLanguage, StringComparison.OrdinalIgnoreCase);
				list.Add(new FloatMenuOption((flag ? "✓  " : "    ") + captured, delegate
				{
					SetTargetLanguage(captured);
				}));
			}
			if (list.Count > 0)
			{
				Find.WindowStack.Add(new FloatMenu(list));
			}
		}

		private void SetTargetLanguage(string language)
		{
			string value = LanguageCodeMapper.Canonical(language);
			if (!string.IsNullOrEmpty(value))
			{
				targetLanguage = value;
				targetLanguageChosen = true;
				RemoveTargetFromSources();
				PersistChoices();
				RebuildInventory();
				status = "Target language changed to " + targetLanguage + ".";
			}
		}

		private void RemoveTargetFromSources()
		{
			if (selectedSourceLanguages == null)
			{
				selectedSourceLanguages = new List<string>();
			}
			selectedSourceLanguages.RemoveAll((string x) => string.Equals(LanguageCodeMapper.Canonical(x), LanguageCodeMapper.Canonical(targetLanguage), StringComparison.OrdinalIgnoreCase));
		}

		private void CycleTargetLanguage()
		{
			ShowTargetLanguageMenu(new Rect(0f, 0f, 1f, 1f));
		}

		private string FormatSelectedSourcesShort()
		{
			if (selectedSourceLanguages == null || selectedSourceLanguages.Count == 0)
			{
				return UiText("URT_UI_NONE");
			}
			if (selectedSourceLanguages.Count <= 2)
			{
				return FormatSelectedSources();
			}
			return selectedSourceLanguages.Count + " languages selected";
		}

		private void DrawSourceLanguageGrid(Rect rect)
		{
			List<string> list = GetSourceLanguageOptions();
			if (list.Count == 0)
			{
				list = LanguageCodeMapper.GetCommonLanguages().ToList();
			}
			List<string> unique = new List<string>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int j = 0; j < list.Count; j++)
			{
				string languageDisplayName = GetLanguageDisplayName(list[j]);
				string text = LanguageCodeMapper.Canonical(list[j]);
				if (string.IsNullOrWhiteSpace(text))
				{
					text = languageDisplayName;
				}
				if (!hashSet.Contains(text) && !hashSet.Contains(languageDisplayName))
				{
					hashSet.Add(text);
					hashSet.Add(languageDisplayName);
					unique.Add(list[j]);
				}
			}
			unique.Sort((string a, string b) => string.Compare(GetLanguageDisplayName(a), GetLanguageDisplayName(b), StringComparison.OrdinalIgnoreCase));
			int num = ((unique.Count > 24) ? 4 : ((unique.Count > 12) ? 3 : 2));
			float num2 = 26f;
			int num3 = (unique.Count + num - 1) / num;
			Rect viewRect = new Rect(0f, 0f, rect.width - 18f, Mathf.Max(rect.height, (float)num3 * num2 + 4f));
			Widgets.BeginScrollView(rect, ref sourceLangScroll, viewRect);
			try
			{
				float num4 = viewRect.width / (float)num;
				int i;
				for (i = 0; i < unique.Count; i++)
				{
					int num5 = i % num;
					int num6 = i / num;
					Rect rect2 = new Rect((float)num5 * num4 + 2f, (float)num6 * num2, num4 - 6f, num2 - 2f);
					bool flag = selectedSourceLanguages.Any((string x) => string.Equals(x, unique[i], StringComparison.OrdinalIgnoreCase) || string.Equals(LanguageCodeMapper.Canonical(x), LanguageCodeMapper.Canonical(unique[i]), StringComparison.OrdinalIgnoreCase));
					bool flag2 = string.Equals(LanguageCodeMapper.Canonical(unique[i]), LanguageCodeMapper.Canonical(targetLanguage), StringComparison.OrdinalIgnoreCase);
					string languageDisplayName2 = GetLanguageDisplayName(unique[i]);
					string label = (flag2 ? ("CIL  " + languageDisplayName2) : (flag ? ("✓  " + languageDisplayName2) : languageDisplayName2));
					Widgets.DrawBoxSolid(rect2, (flag && !flag2) ? new Color(0.13f, 0.24f, 0.17f, 1f) : new Color(0.07f, 0.08f, 0.09f, 1f));
					Widgets.DrawBox(rect2);
					Widgets.Label(new Rect(rect2.x + 6f, rect2.y + 2f, rect2.width - 8f, 20f), label);
					if (flag2 || !Widgets.ButtonInvisible(rect2))
					{
						continue;
					}
					if (string.Equals(unique[i], "Auto", StringComparison.OrdinalIgnoreCase))
					{
						ApplyAutoLanguagesFromMods(startTranslate: false);
					}
					else if (flag)
					{
						selectedSourceLanguages.RemoveAll((string x) => string.Equals(x, unique[i], StringComparison.OrdinalIgnoreCase) || string.Equals(LanguageCodeMapper.Canonical(x), LanguageCodeMapper.Canonical(unique[i]), StringComparison.OrdinalIgnoreCase));
					}
					else
					{
						selectedSourceLanguages.Add(LanguageCodeMapper.Canonical(unique[i]));
					}
					if (!string.Equals(unique[i], "Auto", StringComparison.OrdinalIgnoreCase))
					{
						SetAutoMain(on: false);
					}
					sourceLanguagesChosen = true;
					PersistChoices();
				}
			}
			finally
			{
				Widgets.EndScrollView();
			}
		}

		private string GetLanguageDisplayName(string language)
		{
			return language switch
			{
				"ChineseSimplified" => "Chinese (Simplified)", 
				"ChineseTraditional" => "Chinese (Traditional)", 
				"PortugueseBrazilian" => "Portuguese (Brazil)", 
				"SpanishLatinAmerica" => "Spanish (Latin America)", 
				"English" => "English", 
				"Czech" => "Czech / Čeština", 
				"Slovak" => "Slovak / Slovenčina", 
				"German" => "German / Deutsch", 
				"French" => "French / Français", 
				"Polish" => "Polish / Polski", 
				"Russian" => "Russian / Русский", 
				"Japanese" => "Japanese / 日本語", 
				"Korean" => "Korean / 한국어", 
				"Hungarian" => "Hungarian / Magyar", 
				"Ukrainian" => "Ukrainian / Українська", 
				_ => language, 
			};
		}

		private void RetryLastFailedCandidates()
		{
			if (autoRunning)
			{
				status = "Stop the current automatic translation before fixing failed texts.";
				return;
			}
			List<TranslationCandidate> list = (from g in (batchManager.LastFailedCandidates ?? new List<TranslationCandidate>()).Where((TranslationCandidate c) => c != null && !string.IsNullOrWhiteSpace(c.SourceText)).GroupBy((TranslationCandidate c) => c.ModPackageId + "\n" + c.Type + "\n" + c.DefType + "\n" + c.Key, StringComparer.Ordinal)
				select g.First()).ToList();
			if (list.Count == 0)
			{
				status = "No retryable failed texts remain.";
				return;
			}
			string text = LanguageCodeMapper.Canonical(targetLanguage);
			foreach (TranslationCandidate item in list)
			{
				cache.Remove(item.ModPackageId, item.Type, item.DefType, item.Key, text);
			}
			SaveCache();
			status = "Fixing " + list.Count + " failed texts using the provider fallback chain...";
			StartForcedModTranslation(list, "Fixing failed texts");
		}

		private void DrawProgressCompact(Rect rect)
		{
			if (batchManager.Progress == null)
			{
				Widgets.Label(rect, status ?? UiText("URT_UI_READY"));
				return;
			}
			BatchProgress progress = batchManager.Progress;
			string label = "Progress: " + progress.Processed + " / " + progress.TotalCandidates + "    Translated: " + progress.Translated + "    Other: " + progress.FallbackTranslated + "    Cached: " + progress.CachedSkipped + "    Failed: " + progress.Failed;
			Widgets.Label(new Rect(rect.x, rect.y, rect.width, 23f), label);
			float num = ((progress.TotalCandidates > 0) ? Mathf.Clamp01((float)progress.Processed / (float)progress.TotalCandidates) : 0f);
			Rect rect2 = new Rect(rect.x, rect.y + 28f, rect.width, 12f);
			Widgets.DrawBoxSolid(rect2, new Color(0.055f, 0.06f, 0.065f, 1f));
			if (num > 0f)
			{
				Widgets.DrawBoxSolid(new Rect(rect2.x, rect2.y, rect2.width * num, rect2.height), HudGreen);
			}
			Widgets.DrawBox(rect2);
			string text = "";
			string label2 = "";
			try
			{
				KeyValuePair<string, int>[] array;
				KeyValuePair<string, int>[] array2;
				lock (progress.StatsLock)
				{
					array = ((progress.ProviderTranslated == null) ? new KeyValuePair<string, int>[0] : progress.ProviderTranslated.ToArray());
					array2 = ((progress.ProviderFailed == null) ? new KeyValuePair<string, int>[0] : progress.ProviderFailed.ToArray());
				}
				if (array.Length != 0)
				{
					text = "    Providers: " + string.Join(", ", array.Select((KeyValuePair<string, int> x) => x.Key + "=" + x.Value).ToArray());
				}
				if (array2.Length != 0)
				{
					List<string> list = new List<string>();
					for (int i = 0; i < array2.Length; i++)
					{
						string value = "";
						lock (progress.StatsLock)
						{
							if (progress.ProviderLastError != null)
							{
								progress.ProviderLastError.TryGetValue(array2[i].Key, out value);
							}
						}
						list.Add(array2[i].Key + " failed=" + array2[i].Value + (string.IsNullOrWhiteSpace(value) ? "" : (" (" + value + ")")));
					}
					label2 = string.Join(" | ", list.ToArray());
				}
			}
			catch (InvalidOperationException)
			{
			}
			Widgets.Label(new Rect(rect.x, rect.y + 45f, rect.width, 23f), "Characters sent: " + progress.CharactersSent + (string.IsNullOrEmpty(progress.Status) ? "" : ("    |    " + progress.Status)) + text);
			Widgets.Label(new Rect(rect.x, rect.y + 67f, rect.width, 23f), label2);
			Widgets.Label(new Rect(rect.x, rect.y + 89f, rect.width, 23f), progress.CurrentProvider + "    " + (progress.Status ?? status ?? UiText("URT_UI_READY")));
		}

		private void DrawModsTab(Rect rect)
		{
			Panel(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 5f, rect.height), HudBlue);
			Widgets.Label(new Rect(rect.x + 16f, rect.y + 10f, rect.width - 30f, 25f), UiText("URT_UI_MOD_SELECTION"));
			Widgets.Label(new Rect(rect.x + 16f, rect.y + 35f, rect.width - 30f, 22f), UiText("URT_UI_MOD_SELECTION_DESC"));
			if (ButtonLabel(new Rect(rect.x + 16f, rect.y + 65f, 220f, 36f), UiText("URT_UI_SCAN_STEP1")))
			{
				ScanFromMainButton();
			}
			if (ButtonLabel(new Rect(rect.x + 246f, rect.y + 65f, 180f, 36f), UiText("URT_UI_TRANSLATE_ALL_MODS")))
			{
				selectedMod = "(All mods)";
				status = "Translation scope: all mods.";
			}
			if (ButtonLabel(new Rect(rect.x + 16f, rect.y + 110f, rect.width - 32f, 36f), "↻  " + UiText("URT_UI_RETRANSLATE_NO_TARGET")))
			{
				RetranslateModsWithoutTargetLanguage();
			}
			if (ButtonLabel(new Rect(rect.x + 16f, rect.y + 194f, rect.width - 32f, 36f), "★  " + UiText("URT_UI_TRANSLATE_OUR_MOD")))
			{
				TranslateOurMod();
			}
			if (!string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) && ButtonLabel(new Rect(rect.x + 16f, rect.y + 236f, rect.width - 32f, 36f), "↻  " + UiText("URT_UI_RETRANSLATE_SELECTED")))
			{
				RetranslateSelectedMod();
			}
			if (!string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) && ButtonLabel(new Rect(rect.x + 16f, rect.y + 278f, rect.width - 32f, 36f), "↻  " + UiText("URT_UI_RETRY_FAILED")))
			{
				RetryUntranslatedSelectedMod();
			}
			if (!string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) && ButtonLabel(new Rect(rect.x + 16f, rect.y + 320f, rect.width - 32f, 36f), "▣  " + UiText("URT_UI_OPEN_MOD_EDITOR")))
			{
				activeTab = 2;
				editorSearch = "";
				InvalidateEditorList();
				status = "Editor filtered to all texts of: " + selectedMod;
			}
			float num = ((selectedMod == "(All mods)") ? 154f : 368f);
			modSearch = Widgets.TextField(new Rect(rect.x + 16f, rect.y + num, rect.width - 32f, 32f), modSearch ?? "");
			Widgets.Label(new Rect(rect.x + 16f, rect.y + num + 33f, rect.width - 32f, 22f), UiText("URT_UI_SEARCH_MODS"));
			List<string> list = GetModNames();
			if (!string.IsNullOrWhiteSpace(modSearch))
			{
				string q = modSearch.Trim();
				list = list.Where((string x) => x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
			}
			float num2 = rect.y + num + 61f;
			float num3 = rect.yMax - 34f;
			float num4 = num3 - 8f;
			Rect outRect = new Rect(rect.x + 16f, num2, rect.width - 32f, Mathf.Max(60f, num4 - num2));
			float num5 = (outRect.width - 10f) / 2f;
			int num6 = (list.Count + 1) / 2;
			float height = Math.Max(outRect.height, (float)num6 * 34f + 6f);
			Widgets.BeginScrollView(viewRect: new Rect(0f, 0f, outRect.width - 18f, height), outRect: outRect, scrollPosition: ref modScroll);
			for (int i = 0; i < list.Count; i++)
			{
				int num7 = i % 2;
				int num8 = i / 2;
				Rect rect2 = new Rect(0f + (float)num7 * (num5 + 10f), (float)num8 * 34f, num5, 30f);
				bool flag = string.Equals(list[i], selectedMod, StringComparison.OrdinalIgnoreCase);
				string label = (flag ? ("✓  " + list[i]) : list[i]);
				if (ButtonLabel(rect2, label))
				{
					selectedMod = list[i];
					InvalidateEditorList();
					status = ((selectedMod == "(All mods)") ? "All mods selected." : ("ONLY this mod will be translated: " + selectedMod));
				}
				if (flag)
				{
					Widgets.DrawBoxSolid(new Rect(rect2.x, rect2.yMax - 3f, rect2.width, 3f), HudGreen);
				}
			}
			Widgets.EndScrollView();
			Widgets.DrawBoxSolid(new Rect(rect.x + 10f, num3 - 2f, rect.width - 20f, 2f), HudAccentSoft);
			float num9 = 170f;
			Rect rect3 = new Rect(rect.xMax - num9 - 16f, num3 - 1f, num9, 30f);
			if (ButtonLabel(rect3, "ALL MODS"))
			{
				selectedMod = "(All mods)";
				modScroll = Vector2.zero;
				InvalidateEditorList();
				status = "All mods selected.";
			}
			Widgets.Label(new Rect(rect.x + 16f, num3, rect.width - num9 - 46f, 24f), (selectedMod == "(All mods)") ? "Current scope: ALL MODS" : ("Current scope: ONLY " + selectedMod));
		}

		private void RetryUntranslatedSelectedMod()
		{
			if (autoRunning)
			{
				status = "Stop the current automatic translation before retrying failed texts.";
				return;
			}
			if (string.IsNullOrWhiteSpace(selectedMod) || string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase))
			{
				status = "Select one specific mod first.";
				return;
			}
			EnsureInventoryReadyForTranslation();
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			CachedTranslation value2;
			int num = GetFilteredCandidates().Count((TranslationCandidate c) => !cache.TryGet(c.ModPackageId, c.Type, c.DefType, c.Key, target, out value2) || value2 == null || string.Equals(NormalizeForTranslationComparison(value2.SourceText), NormalizeForTranslationComparison(value2.AutomaticTranslation), StringComparison.OrdinalIgnoreCase));
			int num2 = RemoveInvalidCachedTranslationsForSelectedMod();
			InvalidateEditorList();
			if (num <= 0 && num2 == 0)
			{
				status = "No untranslated or failed texts found for " + selectedMod + ".";
				return;
			}
			SaveCache();
			CachedTranslation value;
			List<TranslationCandidate> list = (from c in GetFilteredCandidates()
				where !cache.TryGet(c.ModPackageId, c.Type, c.DefType, c.Key, target, out value) || value == null || string.Equals(NormalizeForTranslationComparison(value.SourceText), NormalizeForTranslationComparison(value.AutomaticTranslation), StringComparison.OrdinalIgnoreCase)
				where CandidateLanguageOk(c)
				select c).ToList();
			status = "Retrying " + list.Count + " untranslated/failed texts for " + selectedMod + "...";
			StartForcedModTranslation(list, "Retrying untranslated/failed texts");
		}

		private int RemoveInvalidCachedTranslationsForSelectedMod()
		{
			if (string.IsNullOrWhiteSpace(selectedMod))
			{
				return 0;
			}
			string packageId = null;
			if (inventory != null && inventory.Candidates != null)
			{
				packageId = (from x in inventory.Candidates
					where x != null && string.Equals(x.ModName, selectedMod, StringComparison.OrdinalIgnoreCase)
					select x.ModPackageId).FirstOrDefault((string x) => !string.IsNullOrWhiteSpace(x));
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			int num = 0;
			foreach (CachedTranslation item in cache.GetAll())
			{
				if (item != null && (string.Equals(item.ModName, selectedMod, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(packageId) && string.Equals(item.ModPackageId, packageId, StringComparison.OrdinalIgnoreCase))) && string.Equals(LanguageCodeMapper.Canonical(item.TargetLanguage), target, StringComparison.OrdinalIgnoreCase) && string.Equals(NormalizeForTranslationComparison(item.SourceText), NormalizeForTranslationComparison(item.AutomaticTranslation), StringComparison.OrdinalIgnoreCase))
				{
					num++;
				}
			}
			if (num > 0)
			{
				List<CachedTranslation> list = (from x in cache.GetAll()
					where x != null && (string.Equals(x.ModName, selectedMod, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(packageId) && string.Equals(x.ModPackageId, packageId, StringComparison.OrdinalIgnoreCase))) && string.Equals(LanguageCodeMapper.Canonical(x.TargetLanguage), target, StringComparison.OrdinalIgnoreCase)
					select x).ToList();
				cache.RemoveForMod(selectedMod, packageId, target);
				foreach (CachedTranslation item2 in list)
				{
					if (!string.Equals(NormalizeForTranslationComparison(item2.SourceText), NormalizeForTranslationComparison(item2.AutomaticTranslation), StringComparison.OrdinalIgnoreCase))
					{
						cache.Put(item2);
					}
				}
			}
			return num;
		}

		private static string NormalizeForTranslationComparison(string value)
		{
			return (value ?? "").Trim().Replace("\r\n", "\n").Replace("\r", "\n");
		}

		private async void RetranslateSelectedText(CachedTranslation selected)
		{
			if (autoRunning)
			{
				status = "Stop the current automatic translation before re-translating a text.";
				return;
			}
			if (selected == null || string.IsNullOrWhiteSpace(selected.SourceText))
			{
				status = "Select a text first.";
				return;
			}
			EnsureCacheLoaded();
			EnsureInventoryReadyForTranslation();
			if (!ValidateTranslationSetupForSingleText())
			{
				return;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			cache.Remove(selected.ModPackageId, selected.Type, selected.DefType, selected.Key, target);
			SaveCache();
			InvalidateEditorList();
			TranslationCandidate candidate = new TranslationCandidate
			{
				ModName = (selected.ModName ?? ""),
				ModPackageId = (selected.ModPackageId ?? ""),
				Type = (selected.Type ?? ""),
				DefType = (selected.DefType ?? ""),
				Key = (selected.Key ?? ""),
				SourceLanguage = (selected.SourceLanguage ?? "English"),
				SourceText = (selected.SourceText ?? ""),
				FilePath = (selected.FilePath ?? "")
			};
			status = "Re-translating selected text...";
			try
			{
				await RunOneBatch(new List<TranslationCandidate> { candidate }, CancellationToken.None, forceIgnoreCache: true);
				SaveCache();
				TranslationExporter.ExportAll(cache, target);
				InvalidateEditorList();
				status = "Selected text re-translated and exported.";
			}
			catch (Exception ex)
			{
				status = "Selected text re-translation failed: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private bool ValidateTranslationSetupForSingleText()
		{
			if (!HasAnyEnabledProvider())
			{
				status = "Enable at least one translation provider.";
				return false;
			}
			if (selectedSourceLanguages == null || selectedSourceLanguages.Count == 0)
			{
				status = "Select at least one source language.";
				return false;
			}
			if (selectedSourceLanguages.Any((string x) => string.Equals(x, targetLanguage, StringComparison.OrdinalIgnoreCase)))
			{
				status = "Target language cannot also be a source language.";
				return false;
			}
			if (batchManager.Progress != null && batchManager.Progress.Running)
			{
				status = "A batch is already running.";
				return false;
			}
			return true;
		}

		private void RetranslateModsWithoutTargetLanguage()
		{
			if (autoRunning)
			{
				status = "Stop the current automatic translation before starting a bulk retranslation.";
				return;
			}
			if (!HasAnyEnabledProvider())
			{
				status = "Enable at least one translation provider.";
				return;
			}
			EnsureCacheLoaded();
			EnsureInventoryReadyForTranslation();
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			HashSet<string> modsWithoutTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> packageIdsWithoutTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
			{
				if (item == null || string.IsNullOrWhiteSpace(item.RootDir) || !Directory.Exists(item.RootDir))
				{
					continue;
				}
				string path = Path.Combine(item.RootDir, "Languages");
				bool flag = false;
				if (Directory.Exists(path))
				{
					string[] directories = Directory.GetDirectories(path);
					foreach (string path2 in directories)
					{
						string fileName = Path.GetFileName(path2);
						if (string.Equals(LanguageCodeMapper.Canonical(fileName), target, StringComparison.OrdinalIgnoreCase))
						{
							flag = true;
							break;
						}
					}
				}
				if (!flag)
				{
					if (!string.IsNullOrWhiteSpace(item.Name))
					{
						modsWithoutTarget.Add(item.Name);
					}
					if (!string.IsNullOrWhiteSpace(item.PackageIdPlayerFacing))
					{
						packageIdsWithoutTarget.Add(item.PackageIdPlayerFacing);
					}
				}
			}
			if (modsWithoutTarget.Count == 0 && packageIdsWithoutTarget.Count == 0)
			{
				status = "All loaded mods already contain the target language: " + targetLanguage + ".";
				return;
			}
			List<TranslationCandidate> list = new List<TranslationCandidate>();
			IEnumerable<IGrouping<string, TranslationEntry>> enumerable = (from e in database.GetEntries()
				where e != null && !string.Equals(LanguageCodeMapper.Canonical(e.Language), target, StringComparison.OrdinalIgnoreCase) && (modsWithoutTarget.Contains(e.ModName ?? "") || packageIdsWithoutTarget.Contains(e.ModPackageId ?? ""))
				select e).GroupBy((TranslationEntry e) => e.ModPackageId + "\n" + e.Type + "\n" + e.DefType + "\n" + e.Key, StringComparer.Ordinal);
			foreach (IGrouping<string, TranslationEntry> item2 in enumerable)
			{
				TranslationEntry translationEntry = null;
				foreach (string item3 in selectedSourceLanguages ?? new List<string>())
				{
					string canonicalAllowed = LanguageCodeMapper.Canonical(item3);
					translationEntry = item2.FirstOrDefault((TranslationEntry e) => string.Equals(LanguageCodeMapper.Canonical(e.Language), canonicalAllowed, StringComparison.OrdinalIgnoreCase));
					if (translationEntry != null)
					{
						break;
					}
				}
				if (translationEntry != null && !string.IsNullOrWhiteSpace(translationEntry.Text) && !string.IsNullOrWhiteSpace(translationEntry.Key))
				{
					list.Add(new TranslationCandidate
					{
						ModName = translationEntry.ModName,
						ModPackageId = translationEntry.ModPackageId,
						Type = translationEntry.Type,
						DefType = translationEntry.DefType,
						Key = translationEntry.Key,
						SourceLanguage = LanguageCodeMapper.Canonical(translationEntry.Language),
						SourceText = translationEntry.Text,
						FilePath = translationEntry.FilePath
					});
				}
			}
			list = (from g in list.GroupBy((TranslationCandidate c) => c.ModPackageId + "\n" + c.Type + "\n" + c.DefType + "\n" + c.Key, StringComparer.Ordinal)
				select g.First()).OrderBy((TranslationCandidate c) => c.ModName, StringComparer.OrdinalIgnoreCase).ThenBy((TranslationCandidate c) => c.Key, StringComparer.OrdinalIgnoreCase).ToList();
			status = "Retranslating " + list.Count + " texts from mods without " + targetLanguage + "...";
			StartForcedModTranslation(list, "Retranslating mods without target language");
		}

		private void RetranslateSelectedMod()
		{
			if (autoRunning)
			{
				status = "Stop the current automatic translation before re-translating a mod.";
				return;
			}
			if (string.IsNullOrWhiteSpace(selectedMod) || string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase))
			{
				status = "Select one specific mod first.";
				return;
			}
			EnsureCacheLoaded();
			EnsureInventoryReadyForTranslation();
			string modPackageId = null;
			if (inventory != null && inventory.Candidates != null)
			{
				modPackageId = (from x in inventory.Candidates
					where x != null && string.Equals(x.ModName, selectedMod, StringComparison.OrdinalIgnoreCase)
					select x.ModPackageId).FirstOrDefault((string x) => !string.IsNullOrWhiteSpace(x));
			}
			int num = cache.RemoveForMod(selectedMod, modPackageId, LanguageCodeMapper.Canonical(targetLanguage));
			InvalidateEditorList();
			try
			{
				TranslationExporter.ExportAll(cache, LanguageCodeMapper.Canonical(targetLanguage));
			}
			catch (Exception ex)
			{
				status = "Cache cleared, but export cleanup failed: " + ex.GetType().Name + ": " + ex.Message;
				SaveCache();
				return;
			}
			SaveCache();
			List<TranslationCandidate> list = (from c in GetFilteredCandidates()
				where CandidateLanguageOk(c)
				select c).ToList();
			status = "Removed " + num + " cached translations for " + selectedMod + ". Starting a fresh translation of " + list.Count + " texts...";
			StartForcedModTranslation(list, "Retranslating entire mod");
		}

		private void StartForcedModTranslation(List<TranslationCandidate> candidates, string operationName)
		{
			if (autoRunning)
			{
				status = "Another automatic translation is already running.";
				return;
			}
			if (candidates == null || candidates.Count == 0)
			{
				status = operationName + ": no eligible texts found.";
				return;
			}
			if (!HasAnyEnabledProvider())
			{
				status = "Enable at least one translation provider.";
				return;
			}
			autoCancellation = new CancellationTokenSource();
			foreach (TranslationCandidate candidate in candidates)
			{
				string text = AutoCandidateId(candidate);
				if (!string.IsNullOrEmpty(text))
				{
					autoRunFailedCandidates.TryRemove(text, out var _);
				}
			}
			SaveSkipFailed();
			autoRunning = true;
			status = operationName + " started: " + candidates.Count + " texts.";
			RunForcedModTranslationAsync(candidates, operationName, autoCancellation.Token);
		}

		private async void RunForcedModTranslationAsync(List<TranslationCandidate> candidates, string operationName, CancellationToken cancellationToken)
		{
			int processed = 0;
			int batch = 0;
			try
			{
				List<TranslationCandidate> list = await Task.Run(delegate
				{
					List<TranslationCandidate> list2 = candidates.Where((TranslationCandidate c) => c != null && !string.IsNullOrWhiteSpace(c.SourceText)).ToList();
					UrtLivePrewarmPriority.Sort(list2);
					return list2;
				}, cancellationToken);
				List<TranslationCandidate> chunk;
				for (int offset = 0; offset < list.Count; offset += chunk.Count)
				{
					cancellationToken.ThrowIfCancellationRequested();
					chunk = Enumerable.Take(count: Math.Min(GetSelectedAmount(), list.Count - offset), source: list.Skip(offset)).ToList();
					batch++;
					status = operationName + " | batch " + batch + " | " + (processed + 1) + "-" + (processed + chunk.Count) + " / " + candidates.Count + " | -> " + targetLanguage;
					await RunOneBatch(chunk, cancellationToken);
					MarkCurrentAutoBatchFailures(chunk);
					if (batch % 3 == 0)
					{
						await Task.Run(delegate
						{
							SaveCache();
						});
					}
					processed += chunk.Count;
				}
				status = operationName + " complete. Processed: " + processed + " | Cache: " + cache.Count;
				List<TranslationCandidate> bad = new List<TranslationCandidate>();
				foreach (CachedTranslation entry in cache.GetAll())
				{
					if (entry == null || UrtManual.Has(entry.SourceText) || (!LanguageDetector.LooksBadlyTranslated(entry.SourceText, entry.Translation, targetLanguage) && !LanguageDetector.LooksBadlyTranslated(entry.OriginalSourceText, entry.Translation, targetLanguage)))
					{
						continue;
					}
					string original = entry.OriginalSourceText ?? "";
					string current = entry.SourceText ?? "";
					string send = original;
					if (string.IsNullOrWhiteSpace(send))
					{
						send = current;
					}
					else if (LanguageDetector.LooksLikeTargetText(send, targetLanguage) && !LanguageDetector.LooksLikeTargetText(current, targetLanguage) && current.Length > 0)
					{
						send = current;
					}
					if (!string.IsNullOrWhiteSpace(send) && !(send == (entry.Translation ?? "")))
					{
						bad.Add(new TranslationCandidate
						{
							ModName = entry.ModName,
							ModPackageId = entry.ModPackageId,
							Type = entry.Type,
							DefType = entry.DefType,
							Key = entry.Key,
							SourceLanguage = (string.IsNullOrWhiteSpace(entry.SourceLanguage) ? "Auto" : entry.SourceLanguage),
							SourceText = send,
							FilePath = entry.FilePath
						});
						if (bad.Count >= 800)
						{
							break;
						}
					}
				}
				if (bad.Count > 0)
				{
					status = "Auto-fixing " + bad.Count + " bad translations...";
					await RunOneBatch(bad, cancellationToken, forceIgnoreCache: true);
					MarkCurrentAutoBatchFailures(bad);
					await Task.Run(delegate
					{
						SaveCache();
					});
					status = operationName + " complete + auto-fix " + bad.Count + ". Cache: " + cache.Count;
				}
				InvalidateEditorList();
			}
			catch (OperationCanceledException)
			{
				status = operationName + " stopped. Processed: " + processed + " | Cache: " + cache.Count;
			}
			catch (Exception ex3)
			{
				Exception ex = ex3;
				status = operationName + " failed: " + ex.GetType().Name + ": " + ex.Message;
			}
			finally
			{
				await Task.Run(delegate
				{
					SaveCache();
				});
				autoRunning = false;
				if (autoCancellation != null)
				{
					autoCancellation.Dispose();
					autoCancellation = null;
				}
			}
		}

		private void DrawEditorTab(Rect rect)
		{
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(rect);
			listing_Standard.Label(UiText("URT_UI_EDITOR"));
			string text = listing_Standard.TextEntry(editorSearch ?? "");
			if (!string.Equals(text, editorSearch, StringComparison.Ordinal))
			{
				editorSearch = text ?? "";
			}
			listing_Standard.Label(UiText("URT_UI_EDITOR_SEARCH"));
			listing_Standard.Gap(6f);
			string label = (string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) ? "EDITOR FILTER: ALL MODS" : ("EDITOR FILTER: " + selectedMod));
			listing_Standard.Label(label);
			float num = rect.y + listing_Standard.CurHeight + 8f;
			listing_Standard.End();
			float num2 = (rect.width - 24f) / 4f;
			float num3 = num;
			if (ButtonLabelTiny(new Rect(rect.x, num3, num2 - 4f, 26f), UiText("URT_UI_FIND_MOD")))
			{
				if (editorSelectedEntry != null && !string.IsNullOrWhiteSpace(editorSelectedEntry.ModName))
				{
					selectedMod = editorSelectedEntry.ModName;
					activeTab = 1;
					status = "Selected mod: " + selectedMod;
				}
				else
				{
					StartFindModForText(editorSearch);
				}
			}
			if (ButtonLabelTiny(new Rect(rect.x + num2, num3, num2 - 4f, 26f), UiText("URT_UI_ALL_TEXTS")))
			{
				editorSearch = "";
				status = "Showing all texts of: " + selectedMod;
			}
			if (ButtonLabelTiny(new Rect(rect.x + num2 * 2f, num3, num2 - 4f, 26f), UiText("URT_UI_ALL_MODS")))
			{
				selectedMod = "(All mods)";
				editorSearch = "";
				InvalidateEditorList();
				status = "Editor filter cleared: all mods.";
			}
			if (ButtonLabelTiny(new Rect(rect.x + num2 * 3f, num3, num2 - 4f, 26f), UiText("URT_UI_FILES")))
			{
				deepEditorSearchKey = null;
				StartDeepEditorSearchIfNeeded();
			}
			RefreshEditorEntriesIfNeeded();
			List<CachedTranslation> list = editorEntriesCache ?? new List<CachedTranslation>();
			if (!string.IsNullOrWhiteSpace(editorSearch) && list.Count > 0)
			{
				string value = editorSearch.Trim();
				List<CachedTranslation> list2 = new List<CachedTranslation>();
				for (int i = 0; i < list.Count; i++)
				{
					CachedTranslation cachedTranslation = list[i];
					if (cachedTranslation != null && ((cachedTranslation.SourceText ?? "").IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 || (cachedTranslation.Translation ?? "").IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0))
					{
						list2.Add(cachedTranslation);
					}
				}
				list = list2;
			}
			float num4 = num3 + 40f;
			float num5 = Mathf.Max(120f, rect.yMax - num4 - 4f);
			if (list.Count == 0)
			{
				string label2 = (deepEditorSearchRunning ? ("Deep source search running... " + editorSearch) : (editorListBuildInProgress ? UiText("URT_UI_LOADING_EDITOR") : "No translation candidates match the current filter."));
				Widgets.Label(new Rect(rect.x, num4, rect.width, 24f), label2);
				return;
			}
			float num6 = Mathf.Floor(rect.width * 0.36f);
			float num7 = rect.x + num6 + 12f;
			float width = rect.xMax - num7;
			Rect outRect = new Rect(rect.x, num4, num6, num5);
			float num8 = 28f;
			int num9 = Mathf.CeilToInt(num5 / num8) + 2;
			int num10 = Mathf.Max(0, Mathf.FloorToInt(editorScroll.y / num8) - 1);
			int num11 = Mathf.Min(list.Count, num10 + num9);
			Rect viewRect = new Rect(0f, 0f, Mathf.Max(80f, outRect.width - 18f), Mathf.Max(num5, (float)list.Count * num8));
			Widgets.BeginScrollView(outRect, ref editorScroll, viewRect);
			for (int j = num10; j < num11; j++)
			{
				CachedTranslation cachedTranslation2 = list[j];
				if (cachedTranslation2 != null)
				{
					string text2 = OneLine(cachedTranslation2.SourceText, 48);
					string text3 = OneLine(string.IsNullOrEmpty(cachedTranslation2.Translation) ? cachedTranslation2.AutomaticTranslation : cachedTranslation2.Translation, 48);
					string text4 = ((text2.Length == 0) ? GetEditorSourceLabel(cachedTranslation2) : text2);
					if (text3.Length > 0)
					{
						text4 = text4 + "  →  " + text3;
					}
					Rect rect2 = new Rect(0f, (float)j * num8, viewRect.width, 26f);
					if (ButtonLabel(rect2, text4))
					{
						editorSelectedId = EntryId(cachedTranslation2);
						editorSelectedEntry = cachedTranslation2;
						editorDraft = cachedTranslation2.Translation ?? "";
						editorSourceScroll = Vector2.zero;
						editorCurrentScroll = Vector2.zero;
						editorDraftScroll = Vector2.zero;
					}
				}
			}
			Widgets.EndScrollView();
			DrawEditorDetail(new Rect(num7, num4, width, num5));
		}

		private void StartDeepEditorSearchIfNeeded()
		{
			string query = (editorSearch ?? "").Trim();
			if (query.Length < 2)
			{
				return;
			}
			string b = selectedMod + "\n" + query;
			if (string.Equals(deepEditorSearchKey, b, StringComparison.Ordinal))
			{
				return;
			}
			deepEditorSearchKey = b;
			deepEditorEntries.Clear();
			deepEditorSearchRunning = true;
			List<KeyValuePair<string, string>> roots = new List<KeyValuePair<string, string>>();
			try
			{
				foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
				{
					if (item != null && !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.RootDir) && (string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Name, selectedMod, StringComparison.OrdinalIgnoreCase)) && !string.Equals(item.RootDir.TrimEnd('\\', '/'), GetTranslatorRoot().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
					{
						roots.Add(new KeyValuePair<string, string>(item.Name, item.RootDir));
					}
				}
			}
			catch
			{
				deepEditorSearchRunning = false;
				return;
			}
			Task.Run(() => DeepEditorSearchWorker(query, roots)).ContinueWith(delegate(Task<List<CachedTranslation>> t)
			{
				try
				{
					if (!t.IsFaulted && t.Result != null)
					{
						deepEditorEntries = t.Result;
					}
				}
				catch
				{
				}
				deepEditorSearchRunning = false;
				InvalidateEditorList();
			});
		}

		private static List<CachedTranslation> DeepEditorSearchWorker(string query, List<KeyValuePair<string, string>> roots)
		{
			List<CachedTranslation> list = new List<CachedTranslation>();
			string normalizedQuery = NormalizeSearchToken(query);
			foreach (KeyValuePair<string, string> root in roots)
			{
				if (list.Count >= 500)
				{
					break;
				}
				try
				{
					int num = 0;
					foreach (string item in Directory.EnumerateFiles(root.Value, "*", SearchOption.AllDirectories))
					{
						if (++num > 15000)
						{
							break;
						}
						string extension = Path.GetExtension(item);
						if (!IsSearchableModFile(extension))
						{
							continue;
						}
						try
						{
							FileInfo fileInfo = new FileInfo(item);
							if (fileInfo.Length > 33554432)
							{
								continue;
							}
							if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
							{
								XDocument xDocument;
								try
								{
									xDocument = XDocument.Load(item, LoadOptions.PreserveWhitespace);
								}
								catch
								{
									goto end_IL_00a5;
								}
								if (xDocument.Root == null)
								{
									continue;
								}
								foreach (XElement item2 in from x in xDocument.Root.DescendantsAndSelf()
									where !x.HasElements
									select x)
								{
									string value = (item2.Value ?? "").Trim();
									if (!DeepSearchMatches(value, query, normalizedQuery))
									{
										continue;
									}
									CachedTranslation cachedTranslation = BuildDeepXmlEntry(root.Key, item, xDocument.Root, item2, value);
									if (cachedTranslation != null)
									{
										list.Add(cachedTranslation);
										if (list.Count >= 500)
										{
											break;
										}
									}
								}
								goto IL_0373;
							}
							string text = File.ReadAllText(item);
							string[] array = text.Replace("\r\n", "\n").Replace("\r", "\n").Split(new char[1] { '\n' });
							int num2 = 0;
							string[] array2 = array;
							foreach (string text2 in array2)
							{
								num2++;
								string text3 = (text2 ?? "").Trim();
								if (DeepSearchMatches(text3, query, normalizedQuery))
								{
									list.Add(new CachedTranslation
									{
										ModName = root.Key,
										ModPackageId = root.Key,
										Type = "DeepScan",
										DefType = "",
										Key = "DeepScan." + Math.Abs((item + "\n" + num2 + "\n" + text3).GetHashCode()),
										SourceLanguage = "English",
										SourceText = text3,
										TargetLanguage = "English",
										Translation = "",
										AutomaticTranslation = "",
										FilePath = item
									});
									if (list.Count >= 500)
									{
										break;
									}
								}
							}
							goto IL_0373;
							end_IL_00a5:;
						}
						catch
						{
							goto IL_0373;
						}
						continue;
						IL_0373:
						if (list.Count >= 500)
						{
							break;
						}
					}
				}
				catch
				{
				}
			}
			return list;
		}

		private static bool DeepSearchMatches(string value, string query, string normalizedQuery)
		{
			if (string.IsNullOrEmpty(value))
			{
				return false;
			}
			if (value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (string.IsNullOrEmpty(normalizedQuery))
			{
				return false;
			}
			return NormalizeSearchToken(value).Contains(normalizedQuery);
		}

		private static string ExtractSearchLine(string content, string query, string normalizedQuery)
		{
			if (string.IsNullOrEmpty(content))
			{
				return query;
			}
			string[] array = content.Replace("\r\n", "\n").Replace("\r", "\n").Split(new char[1] { '\n' });
			string[] array2 = array;
			foreach (string text in array2)
			{
				if (DeepSearchMatches(text, query, normalizedQuery))
				{
					return text.Trim();
				}
			}
			return query;
		}

		private static string BuildRelativePath(XElement def, XElement leaf)
		{
			if (def == null || leaf == null || def == leaf)
			{
				return (leaf == null) ? "" : leaf.Name.LocalName;
			}
			List<string> list = new List<string>();
			XElement xElement = leaf;
			while (xElement != null && xElement != def)
			{
				list.Add(xElement.Name.LocalName);
				xElement = xElement.Parent;
			}
			if (xElement != def)
			{
				return "";
			}
			list.Reverse();
			return string.Join(".", list);
		}

		private static CachedTranslation BuildDeepXmlEntry(string modName, string file, XElement root, XElement leaf, string value)
		{
			XElement xElement = leaf.AncestorsAndSelf().FirstOrDefault((XElement x) => x.Elements().Any((XElement e) => string.Equals(e.Name.LocalName, "defName", StringComparison.OrdinalIgnoreCase)));
			if (xElement != null)
			{
				string text = xElement.Elements().First((XElement e) => string.Equals(e.Name.LocalName, "defName", StringComparison.OrdinalIgnoreCase)).Value.Trim();
				string text2 = BuildRelativePath(xElement, leaf);
				if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(text2))
				{
					return new CachedTranslation
					{
						ModName = modName,
						ModPackageId = modName,
						Type = "DefInjected",
						DefType = xElement.Name.LocalName,
						Key = text + "." + text2,
						SourceLanguage = "English",
						SourceText = value,
						TargetLanguage = "English",
						Translation = "",
						AutomaticTranslation = "",
						FilePath = file
					};
				}
			}
			if (string.Equals(root.Name.LocalName, "LanguageData", StringComparison.OrdinalIgnoreCase))
			{
				return new CachedTranslation
				{
					ModName = modName,
					ModPackageId = modName,
					Type = "Keyed",
					DefType = "",
					Key = leaf.Name.LocalName,
					SourceLanguage = "English",
					SourceText = value,
					TargetLanguage = "English",
					Translation = "",
					AutomaticTranslation = "",
					FilePath = file
				};
			}
			CachedTranslation cachedTranslation = new CachedTranslation();
			cachedTranslation.ModName = modName;
			cachedTranslation.ModPackageId = modName;
			cachedTranslation.Type = "DeepScan";
			cachedTranslation.DefType = "";
			cachedTranslation.Key = "DeepScan." + Math.Abs((file + "\n" + leaf.Name.LocalName + "\n" + value).GetHashCode());
			cachedTranslation.SourceLanguage = "English";
			cachedTranslation.SourceText = value;
			cachedTranslation.TargetLanguage = "English";
			cachedTranslation.Translation = "";
			cachedTranslation.AutomaticTranslation = "";
			cachedTranslation.FilePath = file;
			return cachedTranslation;
		}

		private void StartFindModForText(string text)
		{
			string query = (text ?? "").Trim();
			if (query.Length < 2)
			{
				modTextSearchResult = "Enter at least 2 characters to find the mod.";
			}
			else
			{
				if (modTextSearchRunning)
				{
					return;
				}
				List<KeyValuePair<string, string>> roots = new List<KeyValuePair<string, string>>();
				try
				{
					foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
					{
						if (item != null && !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.RootDir) && !string.Equals(item.RootDir.TrimEnd('\\', '/'), GetTranslatorRoot().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
						{
							roots.Add(new KeyValuePair<string, string>(item.Name, item.RootDir));
						}
					}
				}
				catch
				{
					modTextSearchResult = "Could not read the loaded mod list.";
					return;
				}
				modTextSearchRunning = true;
				modTextSearchResult = "";
				Task.Run(() => FindModTextWorker(query, roots)).ContinueWith(delegate(Task<string> t)
				{
					try
					{
						modTextSearchResult = (t.IsFaulted ? ("Mod search failed: " + ((t.Exception == null) ? "unknown error" : t.Exception.GetBaseException().Message)) : t.Result);
					}
					catch (Exception ex)
					{
						modTextSearchResult = "Mod search failed: " + ex.Message;
					}
					modTextSearchRunning = false;
				});
			}
		}

		private static string FindModTextWorker(string query, List<KeyValuePair<string, string>> roots)
		{
			string value = NormalizeSearchToken(query);
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, string> root in roots)
			{
				if (list.Count >= 5)
				{
					break;
				}
				try
				{
					int num = 0;
					foreach (string item in Directory.EnumerateFiles(root.Value, "*", SearchOption.AllDirectories))
					{
						if (++num > 2500)
						{
							break;
						}
						string extension = Path.GetExtension(item);
						if (!IsSearchableModFile(extension))
						{
							continue;
						}
						try
						{
							FileInfo fileInfo = new FileInfo(item);
							if (fileInfo.Length <= 12582912)
							{
								bool flag = false;
								if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
								{
									byte[] haystack = File.ReadAllBytes(item);
									flag = ContainsBytes(haystack, Encoding.UTF8.GetBytes(query)) || ContainsBytes(haystack, Encoding.Unicode.GetBytes(query));
								}
								else
								{
									string text = File.ReadAllText(item);
									flag = text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (!string.IsNullOrEmpty(value) && NormalizeSearchToken(text).Contains(value));
								}
								if (flag)
								{
									list.Add(root.Key + " (" + extension.TrimStart(new char[1] { '.' }) + ")");
									break;
								}
							}
						}
						catch
						{
						}
					}
				}
				catch
				{
				}
			}
			if (list.Count == 0)
			{
				return "No loaded mod contains the searched text/key: " + query;
			}
			return "Possible mod(s): " + string.Join(", ", list.ToArray());
		}

		private static bool IsSearchableModFile(string ext)
		{
			switch ((ext ?? "").ToLowerInvariant())
			{
			case ".xml":
			case ".txt":
			case ".json":
			case ".yaml":
			case ".yml":
			case ".csv":
			case ".properties":
			case ".lang":
			case ".cs":
			case ".dll":
				return true;
			default:
				return false;
			}
		}

		private static bool ContainsBytes(byte[] haystack, byte[] needle)
		{
			if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length)
			{
				return false;
			}
			for (int i = 0; i <= haystack.Length - needle.Length; i++)
			{
				int j;
				for (j = 0; j < needle.Length && haystack[i + j] == needle[j]; j++)
				{
				}
				if (j == needle.Length)
				{
					return true;
				}
			}
			return false;
		}

		private static string NormalizeSearchToken(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}
			StringBuilder stringBuilder = new StringBuilder(value.Length);
			string text = value.ToLowerInvariant();
			foreach (char c in text)
			{
				if (char.IsLetterOrDigit(c))
				{
					stringBuilder.Append(c);
				}
			}
			return stringBuilder.ToString();
		}

		private static string StripVanillaSuffix(string text)
		{
			string text2 = (text ?? "").Trim();
			string[] array = new string[5] { " (plán)", " (ve výstavbě)", " (blueprint)", " (in-progress)", " (únava)" };
			string[] array2 = array;
			foreach (string text3 in array2)
			{
				if (text2.EndsWith(text3, StringComparison.OrdinalIgnoreCase))
				{
					return text2.Substring(0, text2.Length - text3.Length).Trim();
				}
			}
			return text2;
		}

		private static string DefaultLanguageText(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
			{
				return null;
			}
			try
			{
				LoadedLanguage defaultLanguage = LanguageDatabase.defaultLanguage;
				if (defaultLanguage == null)
				{
					return null;
				}
				try
				{
					defaultLanguage.LoadData();
				}
				catch
				{
				}
				if (defaultLanguage.keyedReplacements == null)
				{
					return null;
				}
				if (!defaultLanguage.keyedReplacements.TryGetValue(key, out var value) || value == null)
				{
					return null;
				}
				return string.IsNullOrWhiteSpace(value.value) ? null : value.value;
			}
			catch
			{
				return null;
			}
		}

		private static string OneLine(string text, int max)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			string text2 = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
			while (text2.IndexOf("  ", StringComparison.Ordinal) >= 0)
			{
				text2 = text2.Replace("  ", " ");
			}
			if (max > 1 && text2.Length > max)
			{
				text2 = text2.Substring(0, max - 1) + "…";
			}
			return text2;
		}

		private static string TailPath(string path, float width)
		{
			if (string.IsNullOrEmpty(path))
			{
				return "";
			}
			string text = path.Replace('\\', '/');
			GameFont font = Text.Font;
			bool wordWrap = Text.WordWrap;
			Text.Font = GameFont.Tiny;
			Text.WordWrap = false;
			if (Text.CalcSize(text).x > width)
			{
				while (text.Length > 8 && Text.CalcSize("…" + text).x > width)
				{
					text = text.Substring(1);
				}
				text = "…" + text;
			}
			Text.WordWrap = wordWrap;
			Text.Font = font;
			return text;
		}

		private string EditorSourceDisplay(CachedTranslation selected)
		{
			if (selected == null)
			{
				return "";
			}
			string text = StripVanillaSuffix(selected.OriginalSourceText);
			if (!string.IsNullOrWhiteSpace(text) && !LanguageDetector.LooksLikeTargetText(text, targetLanguage))
			{
				return text;
			}
			string text2 = StripVanillaSuffix(selected.SourceText);
			if (!string.IsNullOrWhiteSpace(text2) && !LanguageDetector.LooksLikeTargetText(text2, targetLanguage))
			{
				return text2;
			}
			string text3 = DefaultLanguageText(selected.Key);
			if (!string.IsNullOrWhiteSpace(text3))
			{
				return text3;
			}
			if (!string.IsNullOrWhiteSpace(text))
			{
				return text;
			}
			return text2 ?? "";
		}

		private void DrawEditorDetail(Rect rect)
		{
			CachedTranslation cachedTranslation = editorSelectedEntry;
			if (cachedTranslation == null || !string.Equals(EntryId(cachedTranslation), editorSelectedId, StringComparison.Ordinal))
			{
				cachedTranslation = null;
			}
			if (cachedTranslation == null)
			{
				Widgets.Label(rect, UiText("URT_UI_SELECT_TRANSLATION"));
				return;
			}
			float num = 96f;
			float num2 = rect.yMax - num;
			float y = rect.y;
			Widgets.DrawBoxSolid(new Rect(rect.x, y, rect.width, 58f), new Color(0.09f, 0.095f, 0.105f, 1f));
			Widgets.DrawBox(new Rect(rect.x, y, rect.width, 58f));
			GameFont font = Text.Font;
			bool wordWrap = Text.WordWrap;
			Text.Font = GameFont.Tiny;
			Text.WordWrap = false;
			string label = (cachedTranslation.SourceLanguage ?? "?") + " → " + (cachedTranslation.TargetLanguage ?? "?");
			Widgets.Label(new Rect(rect.x + 8f, y + 4f, rect.width * 0.62f, 16f), OneLine(GetEditorSourceLabel(cachedTranslation), 48));
			Widgets.Label(new Rect(rect.x + rect.width * 0.6f, y + 4f, rect.width * 0.38f, 16f), label);
			string text = UiText("URT_UI_TYPE") + ": " + cachedTranslation.Type;
			if (!string.IsNullOrEmpty(cachedTranslation.DefType))
			{
				text = text + " / " + cachedTranslation.DefType;
			}
			Widgets.Label(new Rect(rect.x + 8f, y + 22f, rect.width - 16f, 16f), OneLine(text, 90));
			string label2 = (string.IsNullOrEmpty(cachedTranslation.FilePath) ? UiText("URT_UI_FILE_RUNTIME") : (UiText("URT_UI_FILE") + ": " + TailPath(cachedTranslation.FilePath, rect.width - 20f)));
			Widgets.Label(new Rect(rect.x + 8f, y + 40f, rect.width - 16f, 16f), label2);
			Text.WordWrap = wordWrap;
			Text.Font = font;
			y += 72f;
			float num3 = num2 - y - 54f - 28f - 8f;
			if (num3 < 90f)
			{
				num3 = 90f;
			}
			float num4 = Mathf.Floor(num3 * 0.3f);
			float num5 = Mathf.Floor(num3 * 0.3f);
			float b = num3 - num4 - num5;
			Text.Font = GameFont.Small;
			Widgets.Label(new Rect(rect.x, y, rect.width, 18f), UiText("URT_UI_SOURCE"));
			y += 18f;
			DrawEditorScrollableText(new Rect(rect.x, y, rect.width, num4), EditorSourceDisplay(cachedTranslation), ref editorSourceScroll);
			y += num4 + 14f;
			Widgets.Label(new Rect(rect.x, y, rect.width, 18f), UiText("URT_UI_AUTOMATIC_CURRENT"));
			y += 18f;
			string text2 = (string.IsNullOrEmpty(cachedTranslation.AutomaticTranslation) ? (cachedTranslation.Translation ?? "") : cachedTranslation.AutomaticTranslation);
			DrawEditorScrollableText(new Rect(rect.x, y, rect.width, num5), text2, ref editorCurrentScroll);
			y += num5 + 14f;
			Widgets.Label(new Rect(rect.x, y, rect.width, 18f), UiText("URT_UI_YOUR_TRANSLATION"));
			y += 18f;
			float num6 = y;
			if (num6 + 36f > num2 - 8f)
			{
				num6 = num2 - 8f - Mathf.Max(36f, b);
			}
			editorDraft = DrawEditorScrollableTextArea(new Rect(rect.x, num6, rect.width, Mathf.Max(36f, num2 - 10f - num6)), editorDraft ?? "", ref editorDraftScroll);
			Text.Font = font;
			float num7 = 8f;
			float num8 = (rect.width - num7) * 0.5f;
			float x = rect.x;
			float x2 = rect.x + num8 + num7;
			if (ButtonLabelTiny(new Rect(x, num2, num8, 28f), UiText("URT_UI_RETRANSLATE_TEXT")))
			{
				RetranslateSelectedText(cachedTranslation);
			}
			if (ButtonLabelTiny(new Rect(x2, num2, num8, 28f), UiText("URT_UI_RETRANSLATE_MOD")))
			{
				selectedMod = cachedTranslation.ModName ?? "";
				InvalidateEditorList();
				RetranslateSelectedMod();
			}
			num2 += 34f;
			if (ButtonLabelTiny(new Rect(x, num2, num8, 28f), UiText("URT_UI_SAVE_MANUAL")))
			{
				cachedTranslation.Translation = StripVanillaSuffix(editorDraft ?? "");
				if (string.IsNullOrWhiteSpace(cachedTranslation.OriginalSourceText) || LanguageDetector.LooksLikeTargetText(cachedTranslation.OriginalSourceText, targetLanguage))
				{
					cachedTranslation.OriginalSourceText = cachedTranslation.SourceText ?? "";
				}
				cache.Put(cachedTranslation);
				UrtManual.Put(cachedTranslation.SourceText, cachedTranslation.Translation);
				RuntimeTextTranslator.RegisterLive(cachedTranslation.SourceText, cachedTranslation.Translation);
				RuntimeTextTranslator.Refresh(cache.GetAll());
				DefTranslationApplier.ApplyOne(cachedTranslation);
				SaveCache();
				ExportOneManual(cachedTranslation);
				InvalidateEditorList();
				status = "Manual translation saved (one entry). Not a full export.";
			}
			num2 += 34f;
			if (ButtonLabelTiny(new Rect(x, num2, num8, 28f), UiText("URT_UI_RESTORE_AUTO")))
			{
				editorDraft = (string.IsNullOrEmpty(cachedTranslation.AutomaticTranslation) ? (cachedTranslation.Translation ?? "") : cachedTranslation.AutomaticTranslation);
				editorDraftScroll = Vector2.zero;
				status = "Editor restored to the last automatic translation.";
			}
			if (ButtonLabelTiny(new Rect(x2, num2, num8, 28f), UiText("URT_UI_RESTORE_SOURCE")))
			{
				editorDraft = cachedTranslation.SourceText ?? "";
				editorDraftScroll = Vector2.zero;
				status = "Editor restored to the original source text.";
			}
		}

		private static void DrawEditorScrollableText(Rect viewport, string text, ref Vector2 scroll)
		{
			string text2 = text ?? "";
			Widgets.DrawBoxSolid(viewport, new Color(0.07f, 0.075f, 0.082f, 1f));
			Widgets.DrawBox(viewport);
			float num = Mathf.Max(40f, viewport.width - 18f);
			float b = Text.CalcHeight(text2, num) + 8f;
			Widgets.BeginScrollView(viewRect: new Rect(0f, 0f, num, Mathf.Max(viewport.height, b)), outRect: viewport, scrollPosition: ref scroll);
			try
			{
				Widgets.Label(new Rect(2f, 2f, num - 4f, Mathf.Max(viewport.height, b)), text2);
			}
			finally
			{
				Widgets.EndScrollView();
			}
		}

		private static string DrawEditorScrollableTextArea(Rect viewport, string text, ref Vector2 scroll)
		{
			string text2 = text ?? "";
			Widgets.DrawBoxSolid(viewport, new Color(0.08f, 0.09f, 0.1f, 1f));
			Widgets.DrawBox(viewport);
			float num = Mathf.Max(40f, viewport.width - 18f);
			float b = Text.CalcHeight(text2, num - 10f) + 14f;
			Widgets.BeginScrollView(viewRect: new Rect(0f, 0f, num, Mathf.Max(viewport.height, b)), outRect: viewport, scrollPosition: ref scroll);
			try
			{
				return GUI.TextArea(new Rect(2f, 2f, num - 4f, Mathf.Max(viewport.height - 4f, b)), text2);
			}
			finally
			{
				Widgets.EndScrollView();
			}
		}

		private void DrawProvidersTab(Rect rect)
		{
			Panel(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 5f, rect.height), HudAccent);
			Widgets.Label(new Rect(rect.x + 16f, rect.y + 10f, rect.width - 30f, 26f), UiText("URT_UI_PROVIDERS"));
			Widgets.Label(new Rect(rect.x + 16f, rect.y + 35f, rect.width - 30f, 22f), UiText("URT_UI_PROVIDERS_DESC"));
			float num = rect.y + 66f;
			float num2 = rect.width - 32f;
			float num3 = (num2 - 10f) * 0.5f;
			float num4 = rect.x + 16f;
			float num5 = num4 + num3 + 10f;
			float num6 = 10f;
			float num7 = 174f;
			float num8 = 138f;
			float height = num7 * 3f + num8 * 7f + num6 * 10f + 330f;
			Rect position = new Rect(rect.x + 8f, num, rect.width - 16f, Mathf.Max(100f, rect.yMax - num - 8f));
			Rect viewRect = new Rect(0f, 0f, rect.width - 32f, height);
			providerScrollPosition = GUI.BeginScrollView(position, providerScrollPosition, viewRect, alwaysShowHorizontal: true, alwaysShowVertical: true);
			try
			{
				float num9 = 0f;
				float num10 = 0f;
				num9 = DrawProviderCard(new Rect(num4 - rect.x - 8f, num9, num3, num7), num9, "1  DeepL", UiText("URT_UI_PRIMARY_API"), enableDeepL, HudAccent, delegate
				{
					enableDeepL = !enableDeepL;
				}, "API key", ref deepLApiKey, mask: true, TestDeepLConnection, "DeepL");
				num10 = DrawProviderCard(new Rect(num5 - rect.x - 8f, num10, num3, num7), num10, "9  Azure Translator", "F0 — key + region global", enableAzure, HudGreen, delegate
				{
					enableAzure = !enableAzure;
				}, "Azure key", ref azureApiKey, mask: true, TestAzureConnection, "Azure");
				Widgets.Label(new Rect(num5 - rect.x - 8f, num10 + 4f, 70f, 22f), "Region");
				azureRegion = Widgets.TextField(new Rect(num5 - rect.x + 66f, num10 + 2f, num3 - 80f, 24f), string.IsNullOrWhiteSpace(azureRegion) ? "global" : azureRegion);
				num10 += 32f;
				Widgets.Label(new Rect(num5 - rect.x - 8f, num10 + 4f, 70f, 22f), "Endpoint");
				azureEndpoint = Widgets.TextField(new Rect(num5 - rect.x + 66f, num10 + 2f, num3 - 80f, 24f), azureEndpoint ?? "");
				num10 += 32f;
				num9 += num6;
				num9 = DrawProviderCard(new Rect(num4 - rect.x - 8f, num9, num3, num7), num9, "2  Google Cloud Translation", UiText("URT_UI_API_FALLBACK"), enableGoogleCloud, HudBlue, delegate
				{
					enableGoogleCloud = !enableGoogleCloud;
				}, "API key", ref googleApiKey, mask: true, TestGoogleConnection, "Google Cloud");
				num9 += num6;
				num9 = DrawSimpleProviderCard(new Rect(num4 - rect.x - 8f, num9, num3, num8), num9, "3  Yandex", "Unofficial, no key - may stop working", enableYandex, HudGreen, delegate
				{
					enableYandex = !enableYandex;
				}, TestYandexConnection, "Yandex");
				num9 += num6;
				num9 = DrawSimpleProviderCard(new Rect(num4 - rect.x - 8f, num9, num3, num8), num9, "3b Google web", "Unofficial, no key - may stop working", enableGoogleWeb, HudGreen, delegate
				{
					enableGoogleWeb = !enableGoogleWeb;
				}, TestGoogleWebConnection, "Google web");
				num9 += num6;
				num9 = DrawSimpleProviderCard(new Rect(num4 - rect.x - 8f, num9, num3, num8), num9, "3c Microsoft Edge", "Unofficial, no key, 100+ languages - may stop working", enableMicrosoftEdge, HudGreen, delegate
				{
					enableMicrosoftEdge = !enableMicrosoftEdge;
				}, TestMicrosoftEdgeConnection, "Microsoft Edge");
				num9 += num6;
				num9 = DrawSimpleProviderCard(new Rect(num4 - rect.x - 8f, num9, num3, num8), num9, "4  SimplyTranslate", UiText("URT_UI_SIMPLY_DESC"), enableSimplyTranslate, HudGreen, delegate
				{
					enableSimplyTranslate = !enableSimplyTranslate;
				}, TestSimplyTranslateConnection, "SimplyTranslate");
				num10 = DrawSimpleProviderCard(new Rect(num5 - rect.x - 8f, num10, num3, num8), num10, "5  MyMemory", UiText("URT_UI_MEMORY_DESC"), enableMyMemory, HudGreen, delegate
				{
					enableMyMemory = !enableMyMemory;
				}, TestMyMemoryConnection, "MyMemory");
				num9 += num6;
				num9 = DrawSimpleProviderCard(new Rect(num4 - rect.x - 8f, num9, num3, num8), num9, "7  Lingva", "Google front, instance often down", enableLingva, HudGreen, delegate
				{
					enableLingva = !enableLingva;
				}, TestLingvaConnection, "Lingva");
				num10 += num6;
				num10 = DrawSimpleProviderCard(new Rect(num5 - rect.x - 8f, num10, num3, num8), num10, "8  Gemini", "AI Studio key below, default OFF", enableGemini, HudGreen, delegate
				{
					enableGemini = !enableGemini;
				}, TestGeminiConnection, "Gemini");
				float num11 = Mathf.Max(num9, num10) + 8f;
				Widgets.Label(new Rect(num5 - rect.x - 8f, num11, 80f, 22f), "Gemini key");
				geminiApiKey = GUI.PasswordField(new Rect(num5 - rect.x + 80f, num11, num3 - 90f, 24f), geminiApiKey ?? "", '*');
				num11 += 30f;
				Widgets.Label(new Rect(num5 - rect.x - 8f, num11, 88f, 22f), "MyMemory e-mail");
				myMemoryEmail = Widgets.TextField(new Rect(num5 - rect.x + 80f, num11, num3 - 90f, 24f), myMemoryEmail ?? "");
				Widgets.Label(new Rect(num5 - rect.x - 8f, num11 + 26f, num3, 22f), "Optional: raises the MyMemory free limit ~5k -> ~50k chars/day.");
				float num12 = num11 + 56f;
				float num13 = num8;
				DrawSimpleProviderCard(new Rect(num5 - rect.x - 8f, num12, num3, num13), num12, "10 Own server", "LibreTranslate or Ollama on your PC - offline, no limits", enableCustomEndpoint, HudGreen, delegate
				{
					enableCustomEndpoint = !enableCustomEndpoint;
				}, TestCustomEndpointConnection, "Custom endpoint");
				num12 += num13 + 6f;
				bool flag = string.Equals(customEndpointType, "Ollama", StringComparison.OrdinalIgnoreCase);
				if (ButtonLabel(new Rect(num5 - rect.x - 8f, num12, num3, 26f), "Type: " + (flag ? "Ollama (local AI)" : "LibreTranslate") + "   (click to switch)", HudBlue, strong: true))
				{
					customEndpointType = (flag ? "LibreTranslate" : "Ollama");
				}
				num12 += 30f;
				Widgets.Label(new Rect(num5 - rect.x - 8f, num12, 80f, 22f), "URL");
				customEndpointUrl = Widgets.TextField(new Rect(num5 - rect.x + 80f, num12, num3 - 90f, 24f), customEndpointUrl ?? "");
				num12 += 28f;
				Widgets.Label(new Rect(num5 - rect.x - 8f, num12, num3, 20f), flag ? "Empty = http://localhost:11434" : "Empty = http://localhost:5000");
				num12 += 22f;
				if (flag)
				{
					Widgets.Label(new Rect(num5 - rect.x - 8f, num12, 80f, 22f), "Model");
					customEndpointModel = Widgets.TextField(new Rect(num5 - rect.x + 80f, num12, num3 - 90f, 24f), customEndpointModel ?? "");
					num12 += 28f;
					Widgets.Label(new Rect(num5 - rect.x - 8f, num12, num3, 20f), "Empty = qwen2.5:7b");
				}
				else
				{
					Widgets.Label(new Rect(num5 - rect.x - 8f, num12, 80f, 22f), "API key");
					customEndpointKey = GUI.PasswordField(new Rect(num5 - rect.x + 80f, num12, num3 - 90f, 24f), customEndpointKey ?? "", '*');
					num12 += 28f;
					Widgets.Label(new Rect(num5 - rect.x - 8f, num12, num3, 20f), "Only if your server requires one");
				}
				num12 += 26f;
				float y = Mathf.Max(num11 + 56f, Mathf.Max(num9 + 8f, num12 + 8f));
				Widgets.Label(new Rect(0f, y, viewRect.width, 24f), "Order: Azure → Microsoft Edge → DeepL → Cloud → Yandex → Google web → Simply → Memory → Lingva → Gemini → Own server");
			}
			finally
			{
				GUI.EndScrollView();
			}
		}

		private float DrawSimpleProviderCard(Rect rect, float currentY, string title, string subtitle, bool enabled, Color accent, Action toggle, Action testAction, string providerName)
		{
			Panel(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 4f, rect.height), accent);
			Widgets.Label(new Rect(rect.x + 12f, rect.y + 7f, rect.width - 120f, 24f), title);
			Widgets.Label(new Rect(rect.x + 12f, rect.y + 31f, rect.width - 120f, 20f), subtitle);
			Rect rect2 = new Rect(rect.xMax - 96f, rect.y + 8f, 82f, 30f);
			if (ButtonLabel(rect2, enabled ? "● ON" : "○ OFF", enabled ? HudGreen : HudRed, enabled))
			{
				toggle();
			}
			Rect rect3 = new Rect(rect.xMax - 96f, rect.y + 42f, 82f, 30f);
			if (ButtonLabel(rect3, "TEST", HudBlue, strong: true))
			{
				GUI.changed = true;
				testAction();
			}
			DrawProviderTestStatus(new Rect(rect.x + 12f, rect.y + 58f, rect.width - 120f, 24f), providerName);
			Widgets.Label(new Rect(rect.x + 12f, rect.y + 91f, rect.width - 24f, 34f), "No account, no key. The provider is used only when earlier providers fail.");
			return currentY + rect.height;
		}

		private float DrawProviderCard(Rect rect, float currentY, string title, string subtitle, bool enabled, Color accent, Action toggle, string fieldLabel, ref string field, bool mask, Action testAction, string providerName)
		{
			Panel(rect);
			Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 4f, rect.height), accent);
			Widgets.Label(new Rect(rect.x + 12f, rect.y + 7f, rect.width - 120f, 24f), title);
			Widgets.Label(new Rect(rect.x + 12f, rect.y + 31f, rect.width - 120f, 20f), subtitle);
			Rect rect2 = new Rect(rect.xMax - 96f, rect.y + 8f, 82f, 30f);
			if (ButtonLabel(rect2, enabled ? "● ON" : "○ OFF", enabled ? HudGreen : HudRed, enabled))
			{
				toggle();
			}
			Rect rect3 = new Rect(rect.xMax - 96f, rect.y + 42f, 82f, 30f);
			if (ButtonLabel(rect3, "TEST", HudBlue, strong: true))
			{
				GUI.changed = true;
				testAction();
			}
			DrawProviderTestStatus(new Rect(rect.x + 12f, rect.y + 58f, rect.width - 120f, 24f), providerName);
			Widgets.Label(new Rect(rect.x + 12f, rect.y + 88f, rect.width - 24f, 18f), fieldLabel);
			Rect rect4 = new Rect(rect.x + 12f, rect.y + 109f, rect.width - 24f, 30f);
			field = (mask ? GUI.PasswordField(rect4, field ?? "", '*') : Widgets.TextField(rect4, field ?? ""));
			return currentY + rect.height;
		}

		private void DrawProviderTestStatus(Rect rect, string providerName)
		{
			if (!providerTestStatus.TryGetValue(providerName ?? "", out var value))
			{
				value = "Not tested";
			}
			if (value.Length > 54)
			{
				value = value.Substring(0, 54) + "...";
			}
			Widgets.DrawBoxSolid(rect, HudPanelAlt);
			Widgets.Label(new Rect(rect.x + 6f, rect.y + 2f, rect.width - 12f, rect.height - 4f), value);
		}

		private void SetProviderTestStatus(string providerName, string message)
		{
			providerTestStatus[providerName ?? ""] = message ?? "";
		}

		private void DrawCacheTab(Rect rect)
		{
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(rect);
			listing_Standard.Label(UiText("URT_UI_LIVE_TITLE"));
			string label = ((!RuntimeLiveQueue.UserEnabled) ? UiText("URT_UI_LIVE_BUTTON_OFF") : (RuntimeLiveQueue.Paused ? UiText("URT_UI_LIVE_PAUSED") : UiText("URT_UI_LIVE_ON")));
			if (Button(listing_Standard, label, 36f))
			{
				if (liveLockedOff || scanRunning)
				{
					enableLiveTranslator = false;
					RuntimeLiveQueue.Pause();
					RuntimeLiveQueue.SetUserEnabled(on: false);
					status = UiText("URT_UI_LIVE_LOCKED");
				}
				else
				{
					enableLiveTranslator = !enableLiveTranslator;
					RuntimeLiveQueue.SetUserEnabled(enableLiveTranslator);
					if (enableLiveTranslator)
					{
						RuntimeLiveQueue.Resume();
						try
						{
							RuntimeTextTranslator.Refresh(cache.GetAll());
						}
						catch
						{
						}
						status = UiText("URT_UI_LIVE_ON_HINT");
					}
					else
					{
						RuntimeLiveQueue.Pause();
						status = UiText("URT_UI_LIVE_OFF");
					}
				}
			}
			UrtLive2.Engaged = useLiveV2;
			if (Button(listing_Standard, useLiveV2 ? "Live engine: v2 (new)" : "Live engine: v1 (old)", 30f))
			{
				useLiveV2 = !useLiveV2;
				UrtLive2.Engaged = useLiveV2;
				UrtLive2.Forget();
				status = (useLiveV2 ? "Live v2 on." : "Live v1 on.");
			}
			if (useLiveV2)
			{
				listing_Standard.Label("Live v2: queued " + UrtLive2.PendingCount);
			}
			if (Button(listing_Standard, writeDefsAtStart ? "Write translations into Defs at start: ON (restart)" : "Write translations into Defs at start: OFF (restart)", 30f))
			{
				writeDefsAtStart = !writeDefsAtStart;
				WriteDefsAtStart = writeDefsAtStart;
				PersistChoices();
				status = "Takes effect after a restart.";
			}
			listing_Standard.Label("Live: čeká " + RuntimeLiveQueue.PendingCount + "  |  hotovo " + RuntimeLiveQueue.DoneCount + "  |  zavřeno " + RuntimeLiveQueue.ClosedCount + (RuntimeLiveQueue.Paused ? "  (pauza)" : ""));
			listing_Standard.Gap();
			listing_Standard.Label(UiText("URT_UI_CACHE_EXPORT"));
			listing_Standard.Label("Cache entries: " + cache.Count);
			listing_Standard.Gap();
			if (Button(listing_Standard, UiText("URT_UI_SCAN_STEP1"), 36f))
			{
				ScanFromMainButton();
			}
			if (Button(listing_Standard, UiText("URT_UI_LOAD_CACHE")))
			{
				LoadCache();
			}
			if (Button(listing_Standard, UiText("URT_UI_SAVE_CACHE")))
			{
				SaveCache();
				status = "Translation cache saved. Entries: " + cache.Count;
			}
			if (Button(listing_Standard, UiText("URT_UI_CLOSE_STUCK")))
			{
				status = "Marked " + AcceptStuckPending() + " stuck texts as done (kept source). Pending should drop.";
			}
			if (Button(listing_Standard, UiText("URT_UI_OPTIMIZE_CACHE")))
			{
				int num = cache.OptimizeJunk();
				int num2 = cache.PurgeBrokenMasks();
				int num3 = cache.RemoveInvalidAutomaticTranslations();
				SaveCache();
				InvalidateEditorList();
				try
				{
					RuntimeTextTranslator.Refresh(cache.GetAll());
				}
				catch
				{
				}
				status = "Cache optimized. Removed junk=" + num + " masks=" + num2 + " unchanged=" + num3 + " | left " + cache.Count;
			}
			if (Button(listing_Standard, UiText("URT_UI_PURGE_DIRTY_CACHE"), 36f))
			{
				if (purgeDirtyRunning)
				{
					status = "Purge is already running.";
				}
				else
				{
					purgeDirtyRunning = true;
					string tgt = targetLanguage;
					status = "Purge started. The game will show a loading screen, that is normal.";
					LongEventHandler.QueueLongEvent(delegate
					{
						int num4 = 0;
						try
						{
							num4 = cache.PurgeDirtyForRetranslate(tgt)?.Count ?? 0;
							SaveCache();
							InvalidateEditorList();
							try
							{
								RuntimeTextTranslator.Refresh(cache.GetAll());
							}
							catch
							{
							}
							status = "b67 purge: removed " + num4 + ", kept " + cache.Count + ". Scan now. Bad cache rows are no longer treated as done.";
							MLog.Info("[URT] " + status);
						}
						catch (Exception ex3)
						{
							status = "Purge failed: " + ex3.GetType().Name + ": " + ex3.Message;
							Log.Error("[URT] " + status);
						}
						finally
						{
							purgeDirtyRunning = false;
						}
					}, "URT purge mixed cache", doAsynchronously: false, null);
				}
			}
			if (Button(listing_Standard, UiText("URT_UI_DELETE_CACHE")))
			{
				Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiText("URT_UI_DELETE_CACHE_CONFIRM"), ClearEntireCache, destructive: true));
			}
			if (Button(listing_Standard, UiText("URT_UI_EXPORT_CACHE")))
			{
				try
				{
					status = TranslationExporter.ExportAll(cache, LanguageCodeMapper.Canonical(targetLanguage));
				}
				catch (Exception ex)
				{
					status = "Export failed: " + ex.GetType().Name + ": " + ex.Message;
				}
			}
			listing_Standard.GapLine();
			listing_Standard.Label("REVIEW / TRANSLATION MEMORY");
			listing_Standard.Label("Failed texts remembered for review: " + autoRunFailedCandidates.Count);
			if (Button(listing_Standard, UiText("URT_UI_EXPORT_REVIEW")))
			{
				ExportReviewCsv();
			}
			if (Button(listing_Standard, UiText("URT_UI_IMPORT_REVIEW")))
			{
				ImportReviewCsv();
			}
			if (Button(listing_Standard, UiText("URT_UI_RELOAD_PACKS")))
			{
				try
				{
					UrtPackLibrary.Reload(targetLanguage);
					status = "Local translation memory reloaded: " + UrtPackLibrary.Status + " Folder: " + UrtPackLibrary.PacksFolder;
				}
				catch (Exception ex2)
				{
					status = "Local translation memory reload failed: " + ex2.GetType().Name + ": " + ex2.Message;
				}
			}
			listing_Standard.Label("Packs folder: " + UrtPackLibrary.PacksFolder);
			listing_Standard.Label("Memory: " + UrtPackLibrary.Status);
			listing_Standard.GapLine();
			listing_Standard.Label(UiText("URT_UI_SCAN_STATS"));
			listing_Standard.Label("Mods scanned: " + ModsScanned);
			listing_Standard.Label("Mods with Languages/: " + ModsWithLanguages);
			listing_Standard.Label("Mods with translatable text: " + ModsWithTranslatableText);
			listing_Standard.Label("Language folders: " + LanguageFolders);
			listing_Standard.Label("XML files scanned: " + FilesScanned);
			listing_Standard.Label("Text entries extracted: " + EntriesExtracted);
			listing_Standard.Label("Translation candidates: " + ((inventory != null && inventory.Candidates != null) ? inventory.Candidates.Count : 0));
			listing_Standard.GapLine();
			listing_Standard.Label(UiText("URT_UI_STATUS"));
			listing_Standard.Label(status ?? "(no status)");
			listing_Standard.End();
		}

		private void DrawTargetLanguage(Listing_Standard l)
		{
			if (Button(l, "Target language: " + targetLanguage + "  [click to change]", 34f))
			{
				CycleTargetLanguage();
			}
		}

		private void DrawProgress(Listing_Standard l)
		{
			if (batchManager.Progress == null)
			{
				return;
			}
			BatchProgress progress = batchManager.Progress;
			l.Label(UiText("URT_UI_CURRENT_RUN"));
			l.Label("Progress: " + progress.Processed + " / " + progress.TotalCandidates);
			l.Label("Translated: " + progress.Translated);
			l.Label("Google fallback: " + progress.FallbackTranslated);
			l.Label("Cached skipped: " + progress.CachedSkipped);
			l.Label("Failed: " + progress.Failed);
			l.Label("Characters sent: " + progress.CharactersSent);
			if (progress.ProviderFailed != null && progress.ProviderFailed.Count > 0)
			{
				foreach (KeyValuePair<string, int> item in progress.ProviderFailed)
				{
					string value = "";
					if (progress.ProviderLastError != null)
					{
						progress.ProviderLastError.TryGetValue(item.Key, out value);
					}
					l.Label(item.Key + " failed: " + item.Value + (string.IsNullOrWhiteSpace(value) ? "" : (" | " + value)));
				}
			}
			if (!string.IsNullOrEmpty(progress.Status))
			{
				l.Label(progress.Status);
			}
			if (progress.Running)
			{
				l.Label(UiText("URT_UI_RUNNING"));
			}
		}

		private List<string> GetModNames()
		{
			if (cachedModNamesReady && Time.realtimeSinceStartup < nextUiRefreshTime)
			{
				return cachedModNames;
			}
			List<string> list = new List<string> { "(All mods)" };
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "(All mods)" };
			string translatorRoot = GetTranslatorRoot();
			if (LoadedModManager.RunningModsListForReading != null)
			{
				foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
				{
					if (item != null && !string.IsNullOrWhiteSpace(item.Name) && (string.IsNullOrEmpty(translatorRoot) || string.IsNullOrEmpty(item.RootDir) || !string.Equals(item.RootDir.TrimEnd('\\', '/'), translatorRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) && hashSet.Add(item.Name))
					{
						list.Add(item.Name);
					}
				}
			}
			cachedModNames = list;
			cachedModNamesReady = true;
			nextUiRefreshTime = Time.realtimeSinceStartup + 1f / 30f;
			return cachedModNames;
		}

		private string GetTranslatorRoot()
		{
			try
			{
				string location = Assembly.GetExecutingAssembly().Location;
				DirectoryInfo directoryInfo = (string.IsNullOrEmpty(location) ? null : Directory.GetParent(location));
				DirectoryInfo directoryInfo2 = ((directoryInfo == null) ? null : Directory.GetParent(directoryInfo.FullName));
				return (directoryInfo2 == null) ? "" : directoryInfo2.FullName;
			}
			catch
			{
				return "";
			}
		}

		private void SelectNextMod()
		{
			List<string> modNames = GetModNames();
			if (modNames.Count != 0)
			{
				int num = modNames.FindIndex((string x) => string.Equals(x, selectedMod, StringComparison.OrdinalIgnoreCase));
				if (num < 0)
				{
					num = 0;
				}
				selectedMod = modNames[(num + 1) % modNames.Count];
				status = ((selectedMod == "(All mods)") ? "All mods selected." : ("Selected mod: " + selectedMod));
			}
		}

		private static bool LooksMostlyTargetSource(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			string text2 = LanguageCodeMapper.Canonical(PlayerTargetLanguage());
			string text3 = LanguageDetector.DetectEmbeddedSourceLanguage(text);
			if (!string.IsNullOrWhiteSpace(text3) && string.Equals(LanguageCodeMapper.Canonical(text3), text2, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (!string.Equals(text2, "Czech", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			int num = 0;
			int num2 = 0;
			foreach (char c in text)
			{
				if (char.IsLetter(c))
				{
					num++;
					if ("áčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ".IndexOf(c) >= 0)
					{
						num2++;
					}
				}
			}
			return num >= 8 && num2 * 4 >= num;
		}

		private void RefreshUiCountsIfNeeded()
		{
			int tickCount = Environment.TickCount;
			int num = (autoRunning ? 3000 : 5000);
			if (uiCountStamp != 0 && tickCount - uiCountStamp < num)
			{
				return;
			}
			uiCountStamp = tickCount;
			if (inventory == null || inventory.Candidates == null)
			{
				uiCandidates = uiScanCandidates;
				uiPending = 0;
				return;
			}
			if (uiScanCandidates <= 0)
			{
				uiScanCandidates = inventory.Candidates.Count;
			}
			uiCandidates = uiScanCandidates;
			RequestPendingRecount();
		}

		private void RequestPendingRecount()
		{
			if (Interlocked.CompareExchange(ref pendingRecountRunning, 1, 0) != 0)
			{
				return;
			}
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					uiPending = CountStillPending();
					uiPendingKnown = true;
				}
				catch (Exception ex)
				{
					if (Interlocked.Increment(ref pendingCountErrorsLogged) <= 3)
					{
						MLog.Warn("[URT] Pending count failed: " + ex.GetType().Name + " " + ex.Message);
					}
					uiCountStamp = 0;
				}
				finally
				{
					Interlocked.Exchange(ref pendingRecountRunning, 0);
				}
			});
		}

		private int CountStillPending()
		{
			TranslationInventory translationInventory = inventory;
			if (translationInventory == null || translationInventory.Candidates == null)
			{
				return 0;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			int num = 0;
			List<TranslationCandidate> candidates = translationInventory.Candidates;
			for (int i = 0; i < candidates.Count; i++)
			{
				TranslationCandidate translationCandidate = candidates[i];
				if (translationCandidate == null || IsAutoCandidateFailed(translationCandidate) || (!string.IsNullOrEmpty(translationCandidate.SourceText) && LooksMostlyTargetSource(translationCandidate.SourceText)))
				{
					continue;
				}
				string text = translationCandidate.ModPackageId ?? "";
				if (!text.StartsWith("Ludeon.", StringComparison.OrdinalIgnoreCase) && (translationCandidate.SourceText ?? "").Trim().Length >= 3)
				{
					bool flag;
					try
					{
						flag = IsReallyCached(translationCandidate, target);
					}
					catch
					{
						flag = false;
					}
					if (!flag)
					{
						num++;
					}
				}
			}
			return num;
		}

		private void NoteScanCandidates()
		{
			MigrateLegacyFailedCandidates();
			uiScanCandidates = ((inventory != null && inventory.Candidates != null) ? inventory.Candidates.Count : 0);
			uiCandidates = uiScanCandidates;
			uiPendingKnown = false;
			uiCountStamp = 0;
			RequestPendingRecount();
			uiCountStamp = Environment.TickCount;
			if (inventory != null)
			{
				MLog.Info("[URT] Inventory keys=" + inventory.TotalKeys + " alreadyTarget=" + inventory.AlreadyTarget + " missing=" + inventory.MissingTarget + " toTranslate=" + ((inventory.Candidates != null) ? inventory.Candidates.Count : 0));
			}
			try
			{
				LogCandidateBreakdown();
			}
			catch
			{
			}
		}

		private void LogCandidateBreakdown()
		{
			if (inventory == null || inventory.Candidates == null || inventory.Candidates.Count == 0)
			{
				return;
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			long num = 0L;
			Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, int> map2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, int> map3 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (TranslationCandidate candidate in inventory.Candidates)
			{
				if (candidate != null)
				{
					string text = candidate.SourceText ?? "";
					if (hashSet.Add(text))
					{
						num += text.Length;
					}
					string key = ((UrtResultValidator.RulePrefixOf(text) != null) ? "grammar rule" : ((!string.Equals(candidate.Type, "Keyed", StringComparison.OrdinalIgnoreCase)) ? ((candidate.DefType ?? "?") + "." + FieldTail(candidate.Key)) : "Keyed"));
					Bump(map, key);
					Bump(map2, string.IsNullOrEmpty(candidate.SourceLanguage) ? "English" : candidate.SourceLanguage);
					Bump(map3, candidate.ModName ?? candidate.ModPackageId ?? "?");
				}
			}
			MLog.Info("[URT] To translate: " + inventory.Candidates.Count + " rows, " + hashSet.Count + " different texts, " + num + " characters. Source languages: " + Top(map2, 8));
			MLog.Info("[URT] To translate by kind: " + Top(map, 25));
			MLog.Info("[URT] To translate by mod: " + Top(map3, 20));
		}

		private static string FieldTail(string key)
		{
			if (string.IsNullOrEmpty(key))
			{
				return "?";
			}
			string[] array = key.Split(new char[1] { '.' });
			for (int num = array.Length - 1; num > 0; num--)
			{
				if (!int.TryParse(array[num], out var _))
				{
					return array[num];
				}
			}
			return array[array.Length - 1];
		}

		private static void Bump(Dictionary<string, int> map, string key)
		{
			map.TryGetValue(key, out var value);
			map[key] = value + 1;
		}

		private static string Top(Dictionary<string, int> map, int count)
		{
			return string.Join(", ", (from kv in map.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).Take(count)
				select kv.Key + " " + kv.Value).ToArray());
		}

		private int MigrateLegacyFailedCandidates()
		{
			if (legacyFailedMigrationAttempted || inventory == null || inventory.Candidates == null)
			{
				return 0;
			}
			legacyFailedMigrationAttempted = true;
			try
			{
				if (File.Exists(LegacySkipMigrationPath))
				{
					return 0;
				}
				if (!File.Exists(LiveSkipFailedPath))
				{
					return 0;
				}
				string[] array = File.ReadAllLines(LiveSkipFailedPath);
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i + 4 < array.Length; i++)
				{
					if (string.Equals(array[i + 1], "Keyed", StringComparison.OrdinalIgnoreCase) || string.Equals(array[i + 1], "DefInjected", StringComparison.OrdinalIgnoreCase))
					{
						hashSet.Add(array[i] + "\n" + array[i + 1] + "\n" + array[i + 2] + "\n" + array[i + 3] + "\n" + array[i + 4]);
					}
				}
				int num = 0;
				string target = LanguageCodeMapper.Canonical(targetLanguage);
				for (int j = 0; j < inventory.Candidates.Count; j++)
				{
					TranslationCandidate translationCandidate = inventory.Candidates[j];
					if (translationCandidate != null)
					{
						string text = AutoCandidateId(translationCandidate);
						if (hashSet.Contains(text))
						{
							autoRunFailedCandidates[text] = UrtOptEngine.Fingerprint(translationCandidate.SourceText, target);
							num++;
						}
					}
				}
				if (num > 0)
				{
					SaveSkipFailed();
					MLog.Info("[URT] Migrated " + num + " old failed candidates into the persistent retry-suppression list.");
				}
				File.WriteAllText(LegacySkipMigrationPath, "b7");
				return num;
			}
			catch
			{
				legacyFailedMigrationAttempted = false;
				return 0;
			}
		}

		private List<TranslationCandidate> GetPendingCandidates()
		{
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			return GetFilteredCandidates().Where(delegate(TranslationCandidate c)
			{
				if (IsAutoCandidateFailed(c))
				{
					return false;
				}
				if (!string.IsNullOrEmpty(c.SourceText) && LooksMostlyTargetSource(c.SourceText))
				{
					return false;
				}
				string text = c.ModPackageId ?? "";
				if (text.StartsWith("Ludeon.", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				return (c.SourceText ?? "").Trim().Length >= 3 && !IsReallyCached(c, target);
			}).ToList();
		}

		private List<TranslationCandidate> GetReviewCandidates()
		{
			if (inventory == null || inventory.Candidates == null)
			{
				return new List<TranslationCandidate>();
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			return (from c in GetFilteredCandidates()
				where c != null && !string.IsNullOrWhiteSpace(c.SourceText)
				where IsAutoCandidateFailed(c)
				where !IsReallyCached(c, target)
				select c).ToList();
		}

		private void ExportReviewCsv()
		{
			try
			{
				List<TranslationCandidate> reviewCandidates = GetReviewCandidates();
				status = TranslationReviewExchange.ExportUnresolved(reviewCandidates, targetLanguage);
			}
			catch (Exception ex)
			{
				status = "Review CSV export failed: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private void ImportReviewCsv()
		{
			try
			{
				ReviewImportResult reviewImportResult = TranslationReviewExchange.ImportReviewed(cache, targetLanguage);
				if (!reviewImportResult.Success)
				{
					status = reviewImportResult.Error;
					return;
				}
				for (int i = 0; i < reviewImportResult.ImportedEntries.Count; i++)
				{
					CachedTranslation cachedTranslation = reviewImportResult.ImportedEntries[i];
					TranslationCandidate candidate = new TranslationCandidate
					{
						ModPackageId = cachedTranslation.ModPackageId,
						Type = cachedTranslation.Type,
						DefType = cachedTranslation.DefType,
						Key = cachedTranslation.Key,
						SourceText = cachedTranslation.SourceText
					};
					autoRunFailedCandidates.TryRemove(AutoCandidateId(candidate), out var _);
				}
				SaveCache();
				InvalidateEditorList();
				RuntimeTextTranslator.Refresh(cache.GetAll());
				LanguageInjection.Inject(cache);
				status = "Review CSV imported: " + reviewImportResult.ImportedEntries.Count + " accepted, " + reviewImportResult.Rejected + " rejected, " + reviewImportResult.Empty + " empty, " + reviewImportResult.WrongLanguage + " for another target.";
			}
			catch (Exception ex)
			{
				status = "Review CSV import failed: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private List<TranslationCandidate> GetFilteredCandidates()
		{
			IEnumerable<TranslationCandidate> source = inventory.Candidates;
			if (!string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase))
			{
				source = source.Where((TranslationCandidate x) => string.Equals(x.ModName, selectedMod, StringComparison.OrdinalIgnoreCase));
			}
			return source.Where((TranslationCandidate x) => x != null && !UrtManual.Has(x.SourceText)).ToList();
		}

		private void InvalidateEditorList()
		{
			editorEntriesCacheKey = null;
			editorListBuildInProgress = false;
			editorBuildMap = null;
			editorBuildSelectedPackageIds = null;
			if (editorBuildCacheEnumerator != null)
			{
				editorBuildCacheEnumerator.Dispose();
				editorBuildCacheEnumerator = null;
			}
			if (editorBuildCandidateEnumerator != null)
			{
				editorBuildCandidateEnumerator.Dispose();
				editorBuildCandidateEnumerator = null;
			}
		}

		private void RefreshEditorEntriesIfNeeded()
		{
			string b = selectedMod + "\n" + editorSearch;
			if (!editorListBuildInProgress && string.Equals(editorEntriesCacheKey, b, StringComparison.Ordinal))
			{
				return;
			}
			if (!editorListBuildInProgress)
			{
				editorBuildKey = b;
				editorBuildMap = new Dictionary<string, CachedTranslation>(StringComparer.Ordinal);
				editorBuildSelectedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (!string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) && inventory != null && inventory.Candidates != null)
				{
					foreach (TranslationCandidate candidate in inventory.Candidates)
					{
						if (candidate != null && !string.IsNullOrWhiteSpace(candidate.ModPackageId) && string.Equals(candidate.ModName, selectedMod, StringComparison.OrdinalIgnoreCase))
						{
							editorBuildSelectedPackageIds.Add(candidate.ModPackageId);
						}
					}
				}
				editorBuildCacheEnumerator = cache.GetAll().GetEnumerator();
				IEnumerable<TranslationCandidate> enumerable;
				if (inventory != null && inventory.Candidates != null)
				{
					IEnumerable<TranslationCandidate> candidates = inventory.Candidates;
					enumerable = candidates;
				}
				else
				{
					enumerable = Enumerable.Empty<TranslationCandidate>();
				}
				editorBuildCandidateEnumerator = enumerable.GetEnumerator();
				editorListBuildInProgress = true;
			}
			int num = 0;
			bool flag = editorBuildCacheEnumerator == null;
			bool flag2 = editorBuildCandidateEnumerator == null;
			while (!flag && num < 500)
			{
				if (!editorBuildCacheEnumerator.MoveNext())
				{
					flag = true;
					break;
				}
				CachedTranslation current2 = editorBuildCacheEnumerator.Current;
				num++;
				if (current2 != null && (string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) || string.Equals(current2.ModName, selectedMod, StringComparison.OrdinalIgnoreCase) || string.Equals(current2.ModPackageId, selectedMod, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(current2.ModName) && current2.ModName.IndexOf(selectedMod, StringComparison.OrdinalIgnoreCase) >= 0) || (!string.IsNullOrWhiteSpace(current2.ModPackageId) && editorBuildSelectedPackageIds.Contains(current2.ModPackageId))))
				{
					editorBuildMap[EntryId(current2)] = current2;
				}
			}
			if (flag)
			{
				while (!flag2 && num < 500)
				{
					if (!editorBuildCandidateEnumerator.MoveNext())
					{
						flag2 = true;
						break;
					}
					TranslationCandidate current3 = editorBuildCandidateEnumerator.Current;
					num++;
					if (current3 != null && !string.IsNullOrWhiteSpace(current3.Key) && (string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) || string.Equals(current3.ModName, selectedMod, StringComparison.OrdinalIgnoreCase)) && !cache.TryGet(current3.ModPackageId, current3.Type, current3.DefType, current3.Key, targetLanguage, out var _))
					{
						CachedTranslation cachedTranslation = new CachedTranslation
						{
							ModName = (current3.ModName ?? ""),
							ModPackageId = current3.ModPackageId,
							Type = current3.Type,
							DefType = current3.DefType,
							Key = current3.Key,
							SourceLanguage = current3.SourceLanguage,
							SourceText = current3.SourceText,
							TargetLanguage = targetLanguage,
							Translation = "",
							AutomaticTranslation = "",
							FilePath = current3.FilePath
						};
						editorBuildMap[EntryId(cachedTranslation)] = cachedTranslation;
					}
				}
			}
			if (!flag || !flag2)
			{
				return;
			}
			if (!string.IsNullOrWhiteSpace(editorSearch) && deepEditorEntries != null)
			{
				foreach (CachedTranslation deepEditorEntry in deepEditorEntries)
				{
					if (deepEditorEntry != null && (string.Equals(selectedMod, "(All mods)", StringComparison.OrdinalIgnoreCase) || string.Equals(deepEditorEntry.ModName, selectedMod, StringComparison.OrdinalIgnoreCase)))
					{
						editorBuildMap[EntryId(deepEditorEntry)] = deepEditorEntry;
					}
				}
			}
			IEnumerable<CachedTranslation> source = editorBuildMap.Values;
			if (!string.IsNullOrWhiteSpace(editorSearch))
			{
				string search = editorSearch.Trim();
				string normalizedSearch = NormalizeSearchToken(search);
				source = source.Where((CachedTranslation x) => (x.SourceText ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (x.Translation ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (!string.IsNullOrEmpty(normalizedSearch) && (NormalizeSearchToken(x.SourceText).Contains(normalizedSearch) || NormalizeSearchToken(x.Translation).Contains(normalizedSearch))));
			}
			editorEntriesCache = source.OrderBy((CachedTranslation x) => x.SourceText, StringComparer.OrdinalIgnoreCase).ToList();
			editorEntriesCacheKey = editorBuildKey;
			editorListBuildInProgress = false;
			editorBuildMap = null;
			editorBuildSelectedPackageIds = null;
			editorBuildCacheEnumerator.Dispose();
			editorBuildCacheEnumerator = null;
			editorBuildCandidateEnumerator.Dispose();
			editorBuildCandidateEnumerator = null;
			if (editorSelectedEntry != null)
			{
				string selectedId = EntryId(editorSelectedEntry);
				editorSelectedEntry = editorEntriesCache.FirstOrDefault((CachedTranslation x) => string.Equals(EntryId(x), selectedId, StringComparison.Ordinal));
				if (editorSelectedEntry == null)
				{
					editorSelectedId = "";
				}
			}
		}

		private static string GetEditorSourceLabel(CachedTranslation entry)
		{
			if (entry == null)
			{
				return "Unknown";
			}
			string text = (entry.ModName ?? "").Trim();
			string text2 = (entry.ModPackageId ?? "").Trim();
			if (string.IsNullOrEmpty(text))
			{
				text = text2;
			}
			if (string.IsNullOrEmpty(text))
			{
				return "Unknown";
			}
			if (string.Equals(text, "Core", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "RimWorld Core", StringComparison.OrdinalIgnoreCase))
			{
				return "RimWorld Core";
			}
			return text;
		}

		private static string EntryId(CachedTranslation x)
		{
			return x.ModPackageId + "\n" + x.Type + "\n" + x.DefType + "\n" + x.Key + "\n" + x.TargetLanguage;
		}

		private List<string> GetTargetLanguageOptions()
		{
			List<string> list = LanguageCodeMapper.GetCommonLanguages().ToList();
			foreach (string language in Languages)
			{
				string canonical = LanguageCodeMapper.Canonical(language);
				if (!string.IsNullOrEmpty(canonical) && LanguageCodeMapper.IsSupported(canonical) && !list.Any((string x) => string.Equals(x, canonical, StringComparison.OrdinalIgnoreCase)))
				{
					list.Add(canonical);
				}
			}
			return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
		}

		private string UiText(string key)
		{
			if (!UiTextMap.TryGetValue(key, out var value) || value == null)
			{
				return key ?? "";
			}
			try
			{
				EnsureCacheLoaded();
				if (cache.TryGet("misak.universalrimtranslator", "Keyed", "", value.Key, targetLanguage, out var value2))
				{
					string text = (string.IsNullOrWhiteSpace(value2.Translation) ? value2.AutomaticTranslation : value2.Translation);
					if (!string.IsNullOrWhiteSpace(text))
					{
						return text;
					}
				}
			}
			catch
			{
			}
			return value.English;
		}

		private string FormatSelectedSources()
		{
			if (selectedSourceLanguages == null || selectedSourceLanguages.Count == 0)
			{
				return UiText("URT_UI_NONE");
			}
			return string.Join(", ", selectedSourceLanguages.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToArray());
		}

		private void TranslateOurMod()
		{
			if (autoRunning)
			{
				status = "Stop the current automatic translation before translating Universal RimTranslator.";
				return;
			}
			try
			{
				EnsureCacheLoaded();
				List<TranslationCandidate> list = (from x in UniversalRimTranslatorUiCatalog.All
					where x != null && !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.English)
					select new TranslationCandidate
					{
						ModName = "Universal RimTranslator",
						ModPackageId = "misak.universalrimtranslator",
						Type = "Keyed",
						DefType = "",
						Key = x.Key,
						SourceLanguage = "English",
						SourceText = x.English,
						FilePath = "Source/UniversalRimTranslatorSettings.cs"
					}).ToList();
				if (list.Count == 0)
				{
					status = "Universal RimTranslator UI catalog is empty.";
					return;
				}
				selectedMod = "Universal RimTranslator";
				InvalidateEditorList();
				status = "Translating Universal RimTranslator UI: " + list.Count + " texts...";
				StartForcedModTranslation(list, "Translating Universal RimTranslator UI");
			}
			catch (Exception ex)
			{
				status = "SELF TRANSLATION PREPARATION FAILED: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private void Scan(bool includeUniversalRimTranslator = false)
		{
			if (scanRunning)
			{
				status = "Scan is already running. Please wait for it to finish.";
				return;
			}
			if ((inventoryBuildTask != null && !inventoryBuildTask.IsCompleted) || (inventoryIncremental != null && !inventoryIncremental.Finished))
			{
				status = "Inventory is still being prepared. Please wait for it to finish.";
				return;
			}
			wideCoverage = true;
			status = "Scanning in background… you can keep playing.";
			cachedModNamesReady = false;
			nextUiRefreshTime = 0f;
			if (Current.ProgramState == ProgramState.Playing)
			{
				Task.Run(delegate
				{
					try
					{
						ScanNow(includeUniversalRimTranslator);
					}
					catch (Exception ex)
					{
						scanRunning = false;
						MLog.Warn("[URT] Background scan failed: " + ex.Message);
					}
				});
			}
			else
			{
				LongEventHandler.QueueLongEvent(delegate
				{
					ScanNow(includeUniversalRimTranslator);
				}, "Universal RimTranslator scan", doAsynchronously: true, null);
			}
		}

		private static void ReopenSettings()
		{
		}

		private int AcceptStuckPending()
		{
			int num = 0;
			try
			{
				List<string> list = new List<string>();
				foreach (TranslationCandidate pendingCandidate in GetPendingCandidates())
				{
					if (pendingCandidate != null && !string.IsNullOrWhiteSpace(pendingCandidate.SourceText))
					{
						autoRunFailedCandidates[AutoCandidateId(pendingCandidate)] = UrtOptEngine.Fingerprint(pendingCandidate.SourceText, targetLanguage);
						list.Add(pendingCandidate.SourceText);
						num++;
					}
				}
				if (num > 0)
				{
					SaveSkipFailed();
					SaveCache();
					InvalidateEditorList();
					try
					{
						RuntimeLiveQueue.CloseMany(list);
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			return num;
		}

		private string DetectAndTranslateNewMods()
		{
			try
			{
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (LoadedModManager.RunningMods != null)
				{
					foreach (ModContentPack runningMod in LoadedModManager.RunningMods)
					{
						if (runningMod != null && !string.IsNullOrWhiteSpace(runningMod.PackageId))
						{
							hashSet.Add(runningMod.PackageId);
						}
					}
				}
				if (hashSet.Count == 0)
				{
					return "";
				}
				HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (File.Exists(KnownModsPath))
				{
					string[] array = File.ReadAllLines(KnownModsPath);
					foreach (string text in array)
					{
						if (!string.IsNullOrWhiteSpace(text))
						{
							hashSet2.Add(text.Trim());
						}
					}
					List<string> added = new List<string>();
					foreach (string item in hashSet)
					{
						if (!hashSet2.Contains(item))
						{
							added.Add(item);
						}
					}
					File.WriteAllLines(KnownModsPath, new List<string>(hashSet).ToArray());
					if (added.Count == 0)
					{
						return "";
					}
					List<TranslationCandidate> list = (from c in GetPendingCandidates()
						where c != null && added.Contains(c.ModPackageId)
						select c).ToList();
					if (list.Count == 0)
					{
						return " New mod(s): " + added.Count + ", nothing pending.";
					}
					if (autoRunning)
					{
						return " New mod texts waiting (translator busy).";
					}
					if (!HasAnyEnabledProvider())
					{
						return " New mod texts found, enable a provider.";
					}
					StartForcedModTranslation(list, "Auto-translating new mods");
					return " Auto-translating " + list.Count + " texts from " + added.Count + " new mod(s).";
				}
				File.WriteAllLines(KnownModsPath, new List<string>(hashSet).ToArray());
				return " Known mods saved. New mods will auto-translate next time.";
			}
			catch
			{
				return "";
			}
		}

		private int PruneUninstalledModsFromCache()
		{
			int num = 0;
			try
			{
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				hashSet.Add("urt.live");
				if (LoadedModManager.RunningMods != null)
				{
					foreach (ModContentPack runningMod in LoadedModManager.RunningMods)
					{
						if (runningMod != null && !string.IsNullOrWhiteSpace(runningMod.PackageId))
						{
							hashSet.Add(runningMod.PackageId);
						}
					}
				}
				HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (CachedTranslation item2 in cache.GetAll())
				{
					if (item2 != null && !string.IsNullOrWhiteSpace(item2.ModPackageId) && !hashSet.Contains(item2.ModPackageId))
					{
						string item = item2.ModPackageId + "|" + item2.ModName;
						if (hashSet2.Add(item))
						{
							cache.RemoveForMod(item2.ModName, item2.ModPackageId, LanguageCodeMapper.Canonical(targetLanguage));
							num++;
						}
					}
				}
				if (num > 0)
				{
					SaveCache();
				}
			}
			catch
			{
			}
			return num;
		}

		private void ExportOneManual(CachedTranslation entry)
		{
			if (entry == null)
			{
				return;
			}
			try
			{
				string text = Path.Combine(GenFilePaths.ConfigFolderPath, "URT_manual");
				Directory.CreateDirectory(text);
				string text2 = (entry.ModPackageId ?? "mod") + "_" + (entry.Key ?? "key");
				char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
				foreach (char oldChar in invalidFileNameChars)
				{
					text2 = text2.Replace(oldChar, '_');
				}
				if (text2.Length > 80)
				{
					text2 = text2.Substring(0, 80);
				}
				File.WriteAllText(Path.Combine(text, text2 + ".txt"), entry.SourceText + "\n---\n" + entry.Translation, Encoding.UTF8);
				UrtManual.Put(entry.SourceText, entry.Translation);
			}
			catch
			{
			}
		}

		private int HarvestEnglishIntoCache()
		{
			int num = 0;
			try
			{
				foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
				{
					if (item == null || string.IsNullOrEmpty(item.RootDir))
					{
						continue;
					}
					string text = item.PackageId ?? item.Name ?? "mod";
					if (text.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					num += HarvestFolder(item, Path.Combine(item.RootDir, "Defs"), "Auto");
					num += HarvestAllLanguages(item, item.RootDir);
					string[] array = (Directory.Exists(item.RootDir) ? Directory.GetDirectories(item.RootDir) : new string[0]);
					for (int i = 0; i < array.Length; i++)
					{
						string fileName = Path.GetFileName(array[i]);
						if (fileName != null && (fileName.StartsWith("1.") || fileName.StartsWith("v")))
						{
							num += HarvestFolder(item, Path.Combine(array[i], "Defs"), "Auto");
							num += HarvestAllLanguages(item, array[i]);
						}
					}
					if (num <= 25000)
					{
						continue;
					}
					break;
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] harvest: " + ex.Message);
			}
			return num;
		}

		private int HarvestAllLanguages(ModContentPack pack, string root)
		{
			int num = 0;
			string path = Path.Combine(root, "Languages");
			if (!Directory.Exists(path))
			{
				return 0;
			}
			string[] directories;
			try
			{
				directories = Directory.GetDirectories(path);
			}
			catch
			{
				return 0;
			}
			string value = (string.IsNullOrWhiteSpace(targetLanguage) ? PlayerTargetLanguage() : LanguageCodeMapper.Canonical(targetLanguage));
			for (int i = 0; i < directories.Length; i++)
			{
				string text = Path.GetFileName(directories[i]) ?? "";
				if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0)
				{
					num += HarvestFolder(pack, directories[i], text);
				}
			}
			return num;
		}

		private int HarvestFolder(ModContentPack pack, string folder, string sourceLang)
		{
			int num = 0;
			if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
			{
				return 0;
			}
			string[] files;
			try
			{
				files = Directory.GetFiles(folder, "*.xml", SearchOption.AllDirectories);
			}
			catch
			{
				return 0;
			}
			for (int i = 0; i < files.Length; i++)
			{
				XDocument xDocument;
				try
				{
					xDocument = XDocument.Load(files[i]);
				}
				catch
				{
					continue;
				}
				if (xDocument.Root == null)
				{
					continue;
				}
				foreach (XElement item in from x in xDocument.Root.Descendants()
					where !x.HasElements
					select x)
				{
					string localName = item.Name.LocalName;
					if (!HarvestTags.Contains(localName) && !string.Equals(xDocument.Root.Name.LocalName, "LanguageData", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					string text = (item.Value ?? "").Trim();
					if (text.Length < 3 || text.Length > 800 || LooksMostlyTargetSource(text))
					{
						continue;
					}
					bool flag = false;
					for (int j = 0; j < text.Length; j++)
					{
						if (text[j] > 'ÿ')
						{
							flag = true;
							break;
						}
					}
					if ((flag || text.IndexOf(' ') >= 0 || text.IndexOf('.') >= 0 || text.Length >= 12) && !text.StartsWith("Color") && (!text.Contains("/") || text.Length >= 40))
					{
						cache.Put(new CachedTranslation
						{
							ModName = pack.Name,
							ModPackageId = pack.PackageId,
							Type = (string.Equals(xDocument.Root.Name.LocalName, "LanguageData", StringComparison.OrdinalIgnoreCase) ? "Keyed" : "DefInjected"),
							DefType = localName,
							Key = "harvest." + UrtHash.Of(pack.PackageId + "|" + localName + "|" + text),
							SourceLanguage = (string.IsNullOrWhiteSpace(sourceLang) ? "Auto" : sourceLang),
							SourceText = text,
							OriginalSourceText = text,
							TargetLanguage = (string.IsNullOrWhiteSpace(targetLanguage) ? PlayerTargetLanguage() : LanguageCodeMapper.Canonical(targetLanguage)),
							Translation = "",
							AutomaticTranslation = "",
							FilePath = files[i]
						});
						num++;
						if (num > 4000)
						{
							return num;
						}
					}
				}
			}
			return num;
		}

		private void ScanNow(bool includeUniversalRimTranslator)
		{
			if (scanRunning)
			{
				return;
			}
			scanRunning = true;
			liveLockedOff = true;
			enableLiveTranslator = false;
			TranslationDatabase translationDatabase = null;
			try
			{
				RuntimeLiveQueue.Pause();
				RuntimeLiveQueue.SetUserEnabled(on: false);
				translationDatabase = new TranslationDatabase();
				EnsureCacheLoaded();
				ModScanner.BeginCacheSkip(cache, targetLanguage);
				ScanSummary scanSummary;
				try
				{
					scanSummary = ModScanner.Scan(translationDatabase, includeUniversalRimTranslator);
				}
				finally
				{
					ModScanner.EndCacheSkip();
				}
				MLog.Info("[Universal RimTranslator] Post-scan: committing database (" + translationDatabase.Count + " entries, skipped cached=" + scanSummary.EntriesSkippedCached + ").");
				database.ReplaceWith(translationDatabase);
				MLog.Info("[Universal RimTranslator] Post-scan: database committed.");
				ModsScanned = scanSummary.ModsScanned;
				ModsWithLanguages = scanSummary.ModsWithLanguages;
				ModsWithTranslatableText = scanSummary.ModsWithTranslatableText;
				LanguageFolders = scanSummary.LanguageFolders;
				FilesScanned = scanSummary.FilesScanned;
				EntriesExtracted = scanSummary.EntriesExtracted;
				Languages = scanSummary.Languages ?? new List<string>();
				if (!sourceLanguagesChosen && selectedSourceLanguages.Count == 0)
				{
					selectedSourceLanguages = new List<string> { "English" };
				}
				RemoveTargetFromSources();
				inventoryReady = false;
				inventory = new TranslationInventory();
				inventoryBuildTargetLanguage = targetLanguage;
				string[] sourceLanguages = (selectedSourceLanguages ?? new List<string>()).ToArray();
				List<TranslationEntry> entries = database.GetEntries();
				if (autoMainLanguage)
				{
					ApplyMainLanguages(entries);
					sourceLanguages = (selectedSourceLanguages ?? new List<string>()).ToArray();
				}
				MLog.Info("[Universal RimTranslator] Post-scan: building inventory in long-event (" + entries.Count + " snapshot entries).");
				inventoryBuildTask = null;
				TranslationDatabase.IncrementalInventoryBuilder incrementalInventoryBuilder = new TranslationDatabase.IncrementalInventoryBuilder(entries, inventoryBuildTargetLanguage, sourceLanguages, cache, autoMainLanguage ? mainLanguageByMod : null);
				inventory = incrementalInventoryBuilder.Finish() ?? new TranslationInventory();
				inventoryIncremental = null;
				inventoryReady = true;
				liveLockedOff = false;
				enableLiveTranslator = true;
				RuntimeLiveQueue.Resume();
				RuntimeLiveQueue.SetUserEnabled(on: true);
				try
				{
					RuntimeTextTranslator.Refresh(cache.GetAll());
				}
				catch
				{
				}
				NoteScanCandidates();
				bool flag = startTranslateAfterScan;
				startTranslateAfterScan = false;
				cachedModNamesReady = false;
				nextUiRefreshTime = 0f;
				status = "Scan complete: " + inventory.Candidates.Count + " candidates for " + inventoryBuildTargetLanguage + ".";
				MLog.Info("[Universal RimTranslator] Inventory ready; Live translator ON (" + inventory.Candidates.Count + " candidates).");
				try
				{
					DefTranslationApplier.ApplyAll(cache);
				}
				catch
				{
				}
				try
				{
					LanguageInjection.HarvestMissing(500);
				}
				catch
				{
				}
				MLog.Info("[Universal RimTranslator] Post-scan: scan work finished; returning control to RimWorld.");
				if (!flag)
				{
					return;
				}
				int num = ((inventory.Candidates != null) ? inventory.Candidates.Count : 0);
				if (num <= 0)
				{
					MLog.Info("[Universal RimTranslator] AUTO chain done: next scan found 0 new candidates.");
					LongEventHandler.ExecuteWhenFinished(delegate
					{
						liveLockedOff = false;
						enableLiveTranslator = true;
						RuntimeLiveQueue.Resume();
						RuntimeLiveQueue.SetUserEnabled(on: true);
						status = "Translate All finished every missing batch. Live ON.";
					});
				}
				else
				{
					MLog.Info("[Universal RimTranslator] AUTO chain: scan found " + num + " more candidates, continuing.");
					LongEventHandler.ExecuteWhenFinished(delegate
					{
						StartAutomaticTranslation();
					});
				}
			}
			catch (Exception ex)
			{
				status = "SCAN FAILED: " + ex.GetType().Name + ": " + ex.Message;
			}
			finally
			{
				scanRunning = false;
				if (inventoryReady && !autoRunning && !liveLockedOff)
				{
					enableLiveTranslator = true;
					RuntimeLiveQueue.Resume();
					RuntimeLiveQueue.SetUserEnabled(on: true);
					MLog.Info("[Universal RimTranslator] Scan finished; Live translator ON.");
				}
			}
		}

		private void RebuildInventory()
		{
			if (selectedSourceLanguages == null || selectedSourceLanguages.Count == 0)
			{
				inventoryBuildTask = null;
				inventoryIncremental = null;
				inventory = new TranslationInventory();
				inventoryReady = true;
				return;
			}
			string text = targetLanguage;
			string[] sourceLanguages = selectedSourceLanguages.ToArray();
			inventoryBuildTargetLanguage = text;
			inventoryReady = false;
			List<TranslationEntry> entries = database.GetEntries();
			inventoryBuildTask = null;
			if (autoMainLanguage && mainLanguageByMod == null)
			{
				ApplyMainLanguages(entries);
				sourceLanguages = (selectedSourceLanguages ?? new List<string>()).ToArray();
			}
			inventoryIncremental = new TranslationDatabase.IncrementalInventoryBuilder(entries, text, sourceLanguages, cache, autoMainLanguage ? mainLanguageByMod : null);
			status = "Building translation inventory...";
		}

		private void PumpInventoryBuild()
		{
			if (inventoryIncremental != null)
			{
				try
				{
					if (!inventoryIncremental.Step(250))
					{
						status = "Building inventory... " + inventoryIncremental.Processed + "/" + inventoryIncremental.TotalEntries;
					}
					else
					{
						inventory = inventoryIncremental.Finish() ?? new TranslationInventory();
						inventoryIncremental = null;
						inventoryReady = true;
						NoteScanCandidates();
						liveLockedOff = false;
						enableLiveTranslator = true;
						RuntimeLiveQueue.Resume();
						RuntimeLiveQueue.SetUserEnabled(on: true);
						try
						{
							RuntimeTextTranslator.Refresh(cache.GetAll());
						}
						catch
						{
						}
						status = "Scan complete: " + inventory.Candidates.Count + " candidates for " + inventoryBuildTargetLanguage + ".";
						MLog.Info("[Universal RimTranslator] Inventory ready; Live translator ON (" + inventory.Candidates.Count + " candidates).");
						if (startTranslateAfterScan)
						{
							startTranslateAfterScan = false;
							StartAutomaticTranslation();
						}
					}
					return;
				}
				catch (Exception ex)
				{
					inventoryIncremental = null;
					inventoryReady = false;
					status = "INVENTORY BUILD FAILED: " + ex.GetType().Name + ": " + ex.Message;
					Log.Error("[Universal RimTranslator] Inventory build failed: " + status);
					return;
				}
			}
			Task<TranslationInventory> task = inventoryBuildTask;
			if (task == null || !task.IsCompleted)
			{
				return;
			}
			inventoryBuildTask = null;
			if (task.IsCanceled)
			{
				inventoryReady = false;
				status = "Inventory build cancelled.";
				return;
			}
			if (task.IsFaulted)
			{
				inventoryReady = false;
				Exception ex2 = ((task.Exception == null) ? null : task.Exception.GetBaseException());
				status = "INVENTORY BUILD FAILED: " + ((ex2 == null) ? "Unknown error." : (ex2.GetType().Name + ": " + ex2.Message));
				Log.Error("[Universal RimTranslator] Inventory build failed: " + status);
				return;
			}
			inventory = task.Result ?? new TranslationInventory();
			inventoryReady = true;
			NoteScanCandidates();
			liveLockedOff = false;
			enableLiveTranslator = true;
			RuntimeLiveQueue.Resume();
			RuntimeLiveQueue.SetUserEnabled(on: true);
			try
			{
				RuntimeTextTranslator.Refresh(cache.GetAll());
			}
			catch
			{
			}
			status = "Scan complete: " + inventory.Candidates.Count + " candidates for " + inventoryBuildTargetLanguage + ".";
			MLog.Info("[Universal RimTranslator] Inventory ready; Live translator remains OFF (" + inventory.Candidates.Count + " candidates).");
			if (startTranslateAfterScan)
			{
				startTranslateAfterScan = false;
				StartAutomaticTranslation();
			}
		}

		private List<string> DetectLanguagesInMods()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			hashSet.Add("English");
			string target = (string.IsNullOrWhiteSpace(targetLanguage) ? PlayerTargetLanguage() : LanguageCodeMapper.Canonical(targetLanguage));
			try
			{
				foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
				{
					if (item == null || string.IsNullOrEmpty(item.RootDir))
					{
						continue;
					}
					AddLangFolders(hashSet, Path.Combine(item.RootDir, "Languages"), target);
					if (!Directory.Exists(item.RootDir))
					{
						continue;
					}
					string[] directories = Directory.GetDirectories(item.RootDir);
					for (int i = 0; i < directories.Length; i++)
					{
						string fileName = Path.GetFileName(directories[i]);
						if (fileName != null && (fileName.StartsWith("1.") || fileName.StartsWith("v")))
						{
							AddLangFolders(hashSet, Path.Combine(directories[i], "Languages"), target);
						}
					}
				}
			}
			catch
			{
			}
			List<string> list = new List<string>(hashSet);
			list.Sort(StringComparer.OrdinalIgnoreCase);
			return list;
		}

		private void AddLangFolders(HashSet<string> found, string langRoot, string target)
		{
			if (!Directory.Exists(langRoot))
			{
				return;
			}
			string[] directories;
			try
			{
				directories = Directory.GetDirectories(langRoot);
			}
			catch
			{
				return;
			}
			for (int i = 0; i < directories.Length; i++)
			{
				string text = Path.GetFileName(directories[i]) ?? "";
				if (text.Length >= 2 && text.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0 && LangFolderHasText(directories[i]))
				{
					string text2 = LanguageCodeMapper.Canonical(text);
					found.Add(string.IsNullOrWhiteSpace(text2) ? text : text2);
				}
			}
		}

		private static bool LangFolderHasText(string dir)
		{
			try
			{
				long num = 0L;
				int num2 = 0;
				string[] files = Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories);
				foreach (string text in files)
				{
					string text2 = Path.GetFileName(text) ?? "";
					if (text2.Equals("LanguageInfo.xml", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					FileInfo fileInfo = new FileInfo(text);
					if (fileInfo.Length >= 80)
					{
						num += fileInfo.Length;
						num2++;
						if (num >= 2048 || num2 >= 3)
						{
							return true;
						}
					}
				}
			}
			catch
			{
			}
			return false;
		}

		private void SetAutoMain(bool on)
		{
			autoMainLanguage = on;
			if (!on)
			{
				mainLanguageByMod = null;
			}
		}

		private void ApplyMainLanguages(List<TranslationEntry> snapshot)
		{
			if (!autoMainLanguage || snapshot == null)
			{
				return;
			}
			Dictionary<string, ModScanner.LanguageStats> lastLanguageStats = ModScanner.LastLanguageStats;
			Dictionary<string, int> modsPerLanguage;
			Dictionary<string, string> dictionary = (mainLanguageByMod = ((lastLanguageStats == null || lastLanguageStats.Count <= 0) ? TranslationDatabase.DetectMainLanguages(snapshot, out modsPerLanguage) : TranslationDatabase.DetectMainLanguages(lastLanguageStats, out modsPerLanguage)));
			string b = LanguageCodeMapper.Canonical(targetLanguage);
			List<string> list = new List<string>();
			foreach (string key in modsPerLanguage.Keys)
			{
				if (!string.Equals(key, b, StringComparison.OrdinalIgnoreCase))
				{
					list.Add(key);
				}
			}
			selectedSourceLanguages = TranslationDatabase.PreferredOrder(list);
			sourceLanguagesChosen = true;
			PersistChoices();
			List<string> list2 = new List<string>();
			foreach (KeyValuePair<string, int> item in modsPerLanguage.OrderByDescending((KeyValuePair<string, int> x) => x.Value))
			{
				list2.Add(item.Key + " " + item.Value);
			}
			MLog.Info("[URT] AUTO main language per mod: " + string.Join(", ", list2.ToArray()) + " (" + dictionary.Count + " mods). Sources: " + string.Join(", ", selectedSourceLanguages.ToArray()));
			int num = 0;
			foreach (KeyValuePair<string, string> item2 in dictionary)
			{
				if (!string.Equals(item2.Value, "English", StringComparison.OrdinalIgnoreCase) && num < 60)
				{
					num++;
					MLog.Info("[URT] AUTO main language " + item2.Value + ": " + ModNameFor(item2.Key));
				}
			}
		}

		private static string ModNameFor(string packageId)
		{
			try
			{
				foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
				{
					if (item != null && string.Equals(item.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
					{
						return item.Name + " (" + packageId + ")";
					}
				}
			}
			catch
			{
			}
			return packageId;
		}

		private void ScanFromMainButton()
		{
			if (autoMainLanguage)
			{
				ApplyAutoLanguagesFromMods(startTranslate: false);
			}
			else
			{
				Scan();
			}
		}

		private void ApplyAutoLanguagesFromMods(bool startTranslate)
		{
			SetAutoMain(on: true);
			selectedSourceLanguages = DetectLanguagesInMods();
			RemoveTargetFromSources();
			selectedSourceLanguages.RemoveAll((string x) => string.Equals(x, "Auto", StringComparison.OrdinalIgnoreCase));
			sourceLanguagesChosen = true;
			PersistChoices();
			status = "AUTO languages: " + string.Join(", ", selectedSourceLanguages.ToArray());
			startTranslateAfterScan = startTranslate;
			Scan();
		}

		private List<string> GetSourceLanguageOptions()
		{
			List<string> list = new List<string>();
			if (Languages != null)
			{
				for (int i = 0; i < Languages.Count; i++)
				{
					string text = Languages[i];
					if (!string.IsNullOrWhiteSpace(text))
					{
						string text2 = LanguageCodeMapper.Canonical(text);
						list.Add(string.IsNullOrWhiteSpace(text2) ? text : text2);
						if (!string.IsNullOrWhiteSpace(text2) && !string.Equals(text2, text, StringComparison.OrdinalIgnoreCase))
						{
							list.Add(text);
						}
					}
				}
			}
			try
			{
				string[] commonLanguages = LanguageCodeMapper.GetCommonLanguages();
				foreach (string text3 in commonLanguages)
				{
					if (!string.IsNullOrWhiteSpace(text3))
					{
						list.Add(text3);
					}
				}
			}
			catch
			{
			}
			list.Add("English");
			List<string> list2 = new List<string>();
			for (int k = 0; k < list.Count; k++)
			{
				string text4 = LanguageCodeMapper.Canonical(list[k]);
				if (string.IsNullOrWhiteSpace(text4))
				{
					text4 = list[k];
				}
				bool flag = false;
				for (int l = 0; l < list2.Count; l++)
				{
					if (string.Equals(LanguageCodeMapper.Canonical(list2[l]), text4, StringComparison.OrdinalIgnoreCase) || string.Equals(list2[l], text4, StringComparison.OrdinalIgnoreCase))
					{
						flag = true;
						break;
					}
				}
				if (!flag)
				{
					list2.Add(text4);
				}
			}
			list2.Sort(StringComparer.OrdinalIgnoreCase);
			return list2;
		}

		private void EnsureInventoryReadyForTranslation()
		{
			if (!inventoryReady || inventory == null || inventory.Candidates == null || inventory.Candidates.Count <= 0)
			{
				Scan();
			}
		}

		public void AcceptLiveTranslation(string source, string translation)
		{
			if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(translation) && !UrtManual.Has(source))
			{
				cache.Put(new CachedTranslation
				{
					ModName = "Live",
					ModPackageId = "urt.live",
					Type = "Keyed",
					DefType = "Live",
					Key = "live." + UrtHash.Of64(source),
					SourceLanguage = (LanguageDetector.DetectEmbeddedSourceLanguage(source) ?? "Auto"),
					SourceText = source,
					OriginalSourceText = source,
					TargetLanguage = (string.IsNullOrWhiteSpace(targetLanguage) ? PlayerTargetLanguage() : LanguageCodeMapper.Canonical(targetLanguage)),
					Translation = translation,
					AutomaticTranslation = translation
				});
				try
				{
					UrtCzechXml.RememberKeyed("URT_" + UrtHash.Of64(source), translation);
				}
				catch
				{
				}
				if (Interlocked.Increment(ref liveDirty) >= 200)
				{
					FlushLiveCache(force: true);
				}
			}
		}

		public void FlushLiveCache()
		{
			FlushLiveCache(force: false);
		}

		public void FlushLiveCache(bool force)
		{
			long elapsedMilliseconds = liveFlushClock.ElapsedMilliseconds;
			if ((!force && elapsedMilliseconds - lastLiveFlushMs < 30000) || Interlocked.Exchange(ref liveDirty, 0) <= 0)
			{
				return;
			}
			lastLiveFlushMs = elapsedMilliseconds;
			lock (liveFlushGate)
			{
				long timestamp = Stopwatch.GetTimestamp();
				try
				{
					cache.SaveLive(LiveCachePath);
				}
				catch
				{
				}
				UrtHitch.Note("SaveLive", Stopwatch.GetTimestamp() - timestamp);
				timestamp = Stopwatch.GetTimestamp();
				try
				{
					UrtCzechXml.Flush();
				}
				catch
				{
				}
				UrtHitch.Note("XmlFlush", Stopwatch.GetTimestamp() - timestamp);
			}
		}

		public void EnsureCacheLoaded()
		{
			if (!cacheLoaded)
			{
				LoadCache(automatic: true);
			}
		}

		private void StartAutoScanIfNeeded()
		{
			if (autoScanQueued || inventoryReady)
			{
				return;
			}
			if (inventory != null && inventory.Candidates != null && inventory.Candidates.Count > 0)
			{
				inventoryReady = true;
				return;
			}
			autoScanQueued = true;
			status = "Auto-scan...";
			LongEventHandler.QueueLongEvent(delegate
			{
				ScanNow(includeUniversalRimTranslator: false);
			}, "Universal RimTranslator auto-scan", doAsynchronously: true, null);
		}

		private void LoadCache()
		{
			LoadCache(automatic: false);
		}

		private void LoadCache(bool automatic)
		{
			try
			{
				cache.Load(CachePath, clearFirst: true);
				int lastRejected = cache.LastRejected;
				cache.Load(LiveCachePath, clearFirst: false);
				lastRejected += cache.LastRejected;
				if (lastRejected > 0)
				{
					try
					{
						cache.Save(CachePath);
					}
					catch
					{
					}
					MLog.Info("[URT] " + lastRejected + " cached translations did not pass the quality check. They are kept on disk and go back into the queue.");
				}
				LoadSkipFailed();
				UrtManual.Load();
				UrtManual.MergeInto(cache, targetLanguage);
				int num = 0;
				cacheLoaded = true;
				InvalidateEditorList();
				if (!automatic)
				{
					status = "Cache loaded: " + cache.Count + " translations." + ((num > 0) ? (" Removed invalid untranslated entries: " + num + ".") : "");
				}
			}
			catch (Exception ex)
			{
				cacheLoaded = false;
				status = "CACHE LOAD FAILED: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private void ClearEntireCache()
		{
			if (autoRunning)
			{
				status = "Stop the current automatic translation before clearing the cache.";
				return;
			}
			try
			{
				int count = cache.Count;
				cache.Clear();
				RuntimeTextTranslator.Refresh(Enumerable.Empty<CachedTranslation>());
				autoRunFailedCandidates.Clear();
				database.Clear();
				inventory = new TranslationInventory();
				inventoryReady = false;
				uiScanCandidates = 0;
				uiCandidates = 0;
				uiPending = 0;
				cachedModNamesReady = false;
				nextUiRefreshTime = 0f;
				cacheLoaded = true;
				editorSelectedEntry = null;
				editorSelectedId = "";
				editorDraft = "";
				deepEditorEntries.Clear();
				deepEditorSearchKey = null;
				modTextSearchResult = "";
				InvalidateEditorList();
				bool flag = DeleteFileIfExists(CachePath);
				bool flag2 = DeleteFileIfExists(LiveCachePath);
				bool flag3 = DeleteFileIfExists(AutoSkipFailedPath);
				bool flag4 = DeleteFileIfExists(LiveSkipFailedPath);
				bool flag5 = DeleteFileIfExists(LegacySkipMigrationPath);
				string path = Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator");
				string path2 = Path.Combine(path, "Export");
				string path3 = Path.Combine(path, "Cache", "UniversalRimTranslator_Diagnostics.txt");
				bool flag6 = DeleteDirectoryIfExists(path2);
				bool flag7 = DeleteFileIfExists(path3);
				string path4 = Path.Combine(path, "Cache");
				bool flag8 = DeleteDirectoryIfExists(path4);
				if (!flag || !flag2 || !flag3 || !flag4 || !flag5 || !flag6 || !flag7 || !flag8 || cache.Count != 0)
				{
					status = "FULL CACHE RESET INCOMPLETE: cache=" + cache.Count + ", cacheFileDeleted=" + flag + ", liveCacheFileDeleted=" + flag2 + ", autoSkipFileDeleted=" + flag3 + ", liveSkipFileDeleted=" + flag4 + ", skipMigrationMarkerDeleted=" + flag5 + ", exportDeleted=" + flag6 + ", diagnosticsDeleted=" + flag7 + ", generatedCacheDirectoryDeleted=" + flag8 + ".";
				}
				else
				{
					UrtManual.Load();
					UrtManual.MergeInto(cache, targetLanguage);
					RuntimeTextTranslator.Refresh(cache.GetAll());
					status = "Cache smazana. Rucni preklady ponechany (" + UrtManual.Count + "). Scan znovu.";
				}
			}
			catch (Exception ex)
			{
				status = "FULL CACHE RESET FAILED: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private static bool DeleteFileIfExists(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
				return !File.Exists(path);
			}
			catch
			{
				return false;
			}
		}

		private static bool DeleteDirectoryIfExists(string path)
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}
				return !Directory.Exists(path);
			}
			catch
			{
				return false;
			}
		}

		private void SaveCache(bool refreshLive = true)
		{
			try
			{
				cache.Save(CachePath);
				cache.SaveLive(LiveCachePath);
				SaveSkipFailed();
				MLog.Info("[URT] Cache saved: " + cache.Count);
				if (refreshLive)
				{
					RefreshRuntimeInBackground();
				}
				cacheLoaded = true;
			}
			catch (Exception ex)
			{
				status = "CACHE SAVE FAILED: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private void RefreshRuntimeInBackground()
		{
			if (!RuntimeTextTranslator.IsInitialized)
			{
				// b114: first Refresh installs Harmony patches; do that on the main thread only
				if (!UrtMainThread.IsMain)
				{
					TranslationCache first = cache;
					UrtMainThread.Post(delegate
					{
						RuntimeTextTranslator.Refresh(first.GetAll());
					});
					return;
				}
				RuntimeTextTranslator.Refresh(cache.GetAll());
				return;
			}
			if (Interlocked.CompareExchange(ref runtimeRefreshRunning, 1, 0) != 0)
			{
				runtimeRefreshAgain = true;
				return;
			}
			TranslationCache c = cache;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					do
					{
						runtimeRefreshAgain = false;
						RuntimeTextTranslator.Refresh(c.GetAll());
					}
					while (runtimeRefreshAgain);
				}
				catch (Exception ex)
				{
					MLog.Warn("[URT] background refresh failed: " + ex.GetType().Name + " " + ex.Message);
				}
				finally
				{
					Interlocked.Exchange(ref runtimeRefreshRunning, 0);
				}
			});
		}

		private bool ProviderTestBusy()
		{
			return providerTestRunning || deepLTestRunning || googleTestRunning;
		}

		internal List<KeyValuePair<string, string>> TakeEntriesForFix(string drawn, string shown, out int manual)
		{
			manual = 0;
			List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
			string b = LanguageCodeMapper.Canonical(PlayerTargetLanguage());
			string text = FixNorm(drawn);
			string b2 = FixNorm(shown);
			bool flag = !string.Equals(text, b2, StringComparison.OrdinalIgnoreCase);
			List<CachedTranslation> list2 = new List<CachedTranslation>();
			foreach (CachedTranslation item in cache.GetAll())
			{
				if (item != null && !string.IsNullOrWhiteSpace(item.Translation) && string.Equals(LanguageCodeMapper.Canonical(item.TargetLanguage), b, StringComparison.OrdinalIgnoreCase))
				{
					string a = FixNorm(item.SourceText);
					string a2 = FixNorm(item.Translation);
					if (string.Equals(a, text, StringComparison.OrdinalIgnoreCase) || (flag && string.Equals(a2, b2, StringComparison.OrdinalIgnoreCase) && string.Equals(a, text, StringComparison.OrdinalIgnoreCase)) || (!flag && string.Equals(a2, text, StringComparison.OrdinalIgnoreCase)))
					{
						list2.Add(item);
					}
					if (list2.Count >= 40)
					{
						break;
					}
				}
			}
			bool flag2 = false;
			foreach (CachedTranslation item2 in list2)
			{
				string text2 = (item2.AutomaticTranslation ?? "").Trim();
				if (text2.Length == 0 || !string.Equals(text2, item2.Translation.Trim(), StringComparison.Ordinal) || UrtManual.Has(item2.SourceText))
				{
					manual++;
				}
				else if (cache.Remove(item2.ModPackageId, item2.Type, item2.DefType, item2.Key, item2.TargetLanguage))
				{
					flag2 = true;
					list.Add(new KeyValuePair<string, string>((item2.SourceText ?? "").Trim(), item2.Translation.Trim()));
					MLog.Info("[URT] fix: removed \"" + UrtFix.Short(item2.SourceText) + "\" -> \"" + UrtFix.Short(item2.Translation) + "\" (" + item2.ModName + ")");
				}
			}
			if (flag2)
			{
				lock (liveFlushGate)
				{
					try
					{
						cache.Save(CachePath);
					}
					catch
					{
					}
					try
					{
						cache.SaveLive(LiveCachePath);
					}
					catch
					{
					}
				}
			}
			return list;
		}

		private static string FixNorm(string text)
		{
			return (text ?? "").Trim().Replace("\r\n", "\n").Replace("\r", "\n");
		}

		internal TranslationProviderSettings BuildProviderSettingsForLive()
		{
			return BuildProviderSettings();
		}

		private TranslationProviderSettings BuildProviderSettings()
		{
			return new TranslationProviderSettings
			{
				DeepLApiKey = (deepLApiKey ?? ""),
				GoogleCloudApiKey = (googleApiKey ?? ""),
				MyMemoryEndpoint = (myMemoryEndpoint ?? ""),
				MyMemoryEmail = (myMemoryEmail ?? ""),
				SimplyTranslateEndpoint = (simplyTranslateEndpoint ?? "https://api.simplytranslate.ai/translate"),
				YandexEndpoint = (yandexEndpoint ?? "https://browser.translate.yandex.net/api/v1/tr.json/translate"),
				EnableDeepL = enableDeepL,
				EnableGoogleCloud = enableGoogleCloud,
				EnableYandex = enableYandex,
				EnableGoogleWeb = enableGoogleWeb,
				EnableSimplyTranslate = enableSimplyTranslate,
				EnableMyMemory = enableMyMemory,
				EnableLingva = enableLingva,
				EnableGemini = enableGemini,
				GeminiApiKey = (geminiApiKey ?? ""),
				EnableAzure = enableAzure,
				AzureApiKey = (azureApiKey ?? ""),
				AzureRegion = (string.IsNullOrWhiteSpace(azureRegion) ? "global" : azureRegion),
				AzureEndpoint = (azureEndpoint ?? ""),
				EnableMicrosoftEdge = enableMicrosoftEdge,
				EnableCustomEndpoint = enableCustomEndpoint,
				CustomEndpointType = (customEndpointType ?? "LibreTranslate"),
				CustomEndpointUrl = (customEndpointUrl ?? ""),
				CustomEndpointModel = (customEndpointModel ?? ""),
				CustomEndpointKey = (customEndpointKey ?? "")
			};
		}

		private TranslationProviderSettings BuildProviderTestSettings()
		{
			return BuildProviderSettings();
		}

		private async void TestYandexConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			SetProviderTestStatus("Yandex", "Testing...");
			UrtOptEngine.ClearCooldown("Yandex");
			try
			{
				TranslationProviderResult r = await yandexProvider.TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, BuildProviderTestSettings(), CancellationToken.None);
				SetProviderTestStatus("Yandex", (r != null && r.Success) ? ("OK: " + ((r.Translations.Count > 0) ? r.Translations[0] : "(empty)")) : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("Yandex", "EXCEPTION: " + ex.GetType().Name + " | " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestGoogleWebConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			SetProviderTestStatus("Google web", "Testing...");
			UrtOptEngine.ClearCooldown("Google web");
			try
			{
				TranslationProviderResult r = await new GoogleWebTranslateProvider().TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, BuildProviderTestSettings(), CancellationToken.None);
				SetProviderTestStatus("Google web", (r != null && r.Success) ? ("OK: " + ((r.Translations.Count > 0) ? r.Translations[0] : "")) : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("Google web", "EXCEPTION: " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestLingvaConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			SetProviderTestStatus("Lingva", "Testing...");
			UrtOptEngine.ClearCooldown("Lingva");
			try
			{
				TranslationProviderResult r = await new LingvaTranslateProvider().TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, BuildProviderTestSettings(), CancellationToken.None);
				SetProviderTestStatus("Lingva", (r != null && r.Success) ? "OK" : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("Lingva", "EXCEPTION: " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestGeminiConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			SetProviderTestStatus("Gemini", "Testing...");
			UrtOptEngine.ClearCooldown("Gemini");
			try
			{
				TranslationProviderSettings s = BuildProviderTestSettings();
				s.GeminiApiKey = geminiApiKey ?? "";
				TranslationProviderResult r = await new GeminiTranslateProvider().TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, s, CancellationToken.None);
				SetProviderTestStatus("Gemini", (r != null && r.Success) ? "OK" : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("Gemini", "EXCEPTION: " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestAzureConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			SetProviderTestStatus("Azure", "Testing...");
			UrtOptEngine.ClearCooldown("Azure Translator");
			try
			{
				TranslationProviderSettings s = BuildProviderTestSettings();
				s.AzureApiKey = azureApiKey ?? "";
				s.AzureRegion = (string.IsNullOrWhiteSpace(azureRegion) ? "global" : azureRegion);
				s.AzureEndpoint = azureEndpoint ?? "";
				TranslationProviderResult r = await new AzureTranslateProvider().TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, s, CancellationToken.None);
				SetProviderTestStatus("Azure", (r != null && r.Success) ? ("OK: " + ((r.Translations.Count > 0) ? r.Translations[0] : "")) : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("Azure", "EXCEPTION: " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestSimplyTranslateConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			SetProviderTestStatus("SimplyTranslate", "Testing...");
			UrtOptEngine.ClearCooldown("SimplyTranslate");
			try
			{
				TranslationProviderResult r = await simplyTranslateProvider.TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, BuildProviderTestSettings(), CancellationToken.None);
				SetProviderTestStatus("SimplyTranslate", (r != null && r.Success) ? ("OK: " + ((r.Translations.Count > 0) ? r.Translations[0] : "(empty)")) : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("SimplyTranslate", "EXCEPTION: " + ex.GetType().Name + " | " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestMyMemoryConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			SetProviderTestStatus("MyMemory", "Testing...");
			UrtOptEngine.ClearCooldown("MyMemory");
			try
			{
				TranslationProviderResult r = await myMemoryProvider.TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, BuildProviderTestSettings(), CancellationToken.None);
				SetProviderTestStatus("MyMemory", (r != null && r.Success) ? ("OK: " + ((r.Translations.Count > 0) ? r.Translations[0] : "(empty)")) : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("MyMemory", "EXCEPTION: " + ex.GetType().Name + " | " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestMicrosoftEdgeConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			string name = "Microsoft Edge";
			SetProviderTestStatus(name, "Testing...");
			UrtOptEngine.ClearCooldown(name);
			try
			{
				TranslationProviderSettings s = BuildProviderTestSettings();
				TranslationProviderResult r = await new MicrosoftEdgeTranslateProvider().TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, s, CancellationToken.None);
				SetProviderTestStatus(name, (r != null && r.Success) ? ("OK: " + r.Translations[0]) : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus(name, "EXCEPTION: " + ex.GetType().Name + " | " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestCustomEndpointConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			providerTestRunning = true;
			string name = "Custom endpoint";
			SetProviderTestStatus(name, "Testing...");
			UrtOptEngine.ClearCooldown(name);
			try
			{
				TranslationProviderSettings s = BuildProviderTestSettings();
				s.CustomEndpointType = customEndpointType ?? "LibreTranslate";
				s.CustomEndpointUrl = customEndpointUrl ?? "";
				s.CustomEndpointModel = customEndpointModel ?? "";
				s.CustomEndpointKey = customEndpointKey ?? "";
				TranslationProviderResult r = await new CustomEndpointTranslateProvider().TranslateBatchAsync(new List<string> { "Hello" }, "English", targetLanguage, s, CancellationToken.None);
				SetProviderTestStatus(name, (r != null && r.Success) ? ("OK: " + r.Translations[0]) : ("FAILED: " + ((r == null) ? "NULL" : r.Error)));
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus(name, "EXCEPTION: " + ex.GetType().Name + " | " + ex.Message);
			}
			finally
			{
				providerTestRunning = false;
			}
		}

		private async void TestDeepLConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			if (string.IsNullOrWhiteSpace(deepLApiKey))
			{
				SetProviderTestStatus("DeepL", "Key is empty");
				return;
			}
			deepLTestRunning = true;
			SetProviderTestStatus("DeepL", "Testing...");
			try
			{
				string source = LanguageCodeMapper.ToDeepLCode("English");
				string target = LanguageCodeMapper.ToDeepLCode(targetLanguage);
				DeepLBatchResult r = await provider.TranslateBatchAsync(deepLApiKey.Trim(), new List<string> { "Hello" }, source, target);
				if (r?.Success ?? false)
				{
					SetProviderTestStatus("DeepL", "OK: " + ((r.Translations.Count > 0) ? r.Translations[0] : "(empty)"));
				}
				else
				{
					SetProviderTestStatus("DeepL", "FAILED: " + ((r == null) ? "NULL" : r.Error));
				}
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("DeepL", "EXCEPTION: " + ex.GetType().Name);
			}
			finally
			{
				deepLTestRunning = false;
			}
		}

		private async void TestGoogleConnection()
		{
			if (ProviderTestBusy())
			{
				return;
			}
			if (string.IsNullOrWhiteSpace(googleApiKey))
			{
				SetProviderTestStatus("Google Cloud", "Key is empty");
				return;
			}
			googleTestRunning = true;
			SetProviderTestStatus("Google Cloud", "Testing...");
			try
			{
				string source = LanguageCodeMapper.ToGoogleCode("English");
				string target = LanguageCodeMapper.ToGoogleCode(targetLanguage);
				DeepLBatchResult r = await fallbackProvider.TranslateBatchAsync(googleApiKey.Trim(), new List<string> { "Hello" }, source, target);
				if (r?.Success ?? false)
				{
					SetProviderTestStatus("Google Cloud", "OK: " + ((r.Translations.Count > 0) ? r.Translations[0] : "(empty)"));
				}
				else
				{
					SetProviderTestStatus("Google Cloud", "FAILED: " + ((r == null) ? "NULL" : r.Error));
				}
			}
			catch (Exception ex2)
			{
				Exception ex = ex2;
				SetProviderTestStatus("Google Cloud", "EXCEPTION: " + ex.GetType().Name);
			}
			finally
			{
				googleTestRunning = false;
			}
		}

		private async void TranslateNextBatch()
		{
			if (autoRunning)
			{
				status = "Automatic translation is already running.";
				return;
			}
			ClearStuckBatchFlag();
			EnsureInventoryReadyForTranslation();
			if (!ValidateTranslationSetup())
			{
				return;
			}
			List<TranslationCandidate> group = GetNextSourceLanguageGroup();
			if (group == null || group.Count == 0)
			{
				status = "Nothing new to translate.";
				return;
			}
			status = "Starting manual translation...";
			await RunOneBatch(group, CancellationToken.None, forceIgnoreCache: true);
			SaveCache();
			RemoveFinishedCandidates(group);
			DropBatchFromInventory(group);
			if (batchManager.Progress != null)
			{
				RequestPendingRecount();
				uiCandidates = ((uiScanCandidates > 0) ? uiScanCandidates : inventory.Candidates.Count);
				uiCountStamp = Environment.TickCount;
			}
			status = batchManager.Progress.Status + " | Auto export OK | Cache total: " + cache.Count;
		}

		private bool HasAnyEnabledProvider()
		{
			return enableAzure || enableDeepL || enableGoogleCloud || enableYandex || enableSimplyTranslate || enableMyMemory || enableLingva || enableGemini || enableMicrosoftEdge || enableGoogleWeb || enableCustomEndpoint;
		}

		private bool ValidateTranslationSetup()
		{
			if (!HasAnyEnabledProvider())
			{
				status = "Enable at least one translation provider.";
				return false;
			}
			if (selectedSourceLanguages == null || selectedSourceLanguages.Count == 0)
			{
				status = "Select at least one source language.";
				return false;
			}
			if (selectedSourceLanguages.Any((string x) => string.Equals(x, targetLanguage, StringComparison.OrdinalIgnoreCase)))
			{
				status = "Target language cannot also be a source language.";
				return false;
			}
			if (inventory == null || inventory.Candidates == null || inventory.Candidates.Count == 0)
			{
				status = "No untranslated candidates for the current mod/source selection.";
				return false;
			}
			if (batchManager.Progress != null && batchManager.Progress.Running)
			{
				status = "A batch is already running.";
				return false;
			}
			return true;
		}

		private void StartAutomaticTranslation()
		{
			if (autoRunning)
			{
				return;
			}
			if (scanRunning)
			{
				status = "Scan still running. Wait, then Translate All.";
				return;
			}
			ClearStuckBatchFlag();
			EnsureInventoryReadyForTranslation();
			if (ValidateTranslationSetup())
			{
				autoCancellation = new CancellationTokenSource();
				autoCandidateIndex = 0;
				autoBatchesSinceSave = 0;
				autoCursorWrapped = false;
				autoStableQueue = null;
				autoStablePos = 0;
				autoRunRetryCounts.Clear();
				autoProviderWaits = 0;
				autoRunning = true;
				TranslationBatchManager.ClearRunSkips();
				if (enableLiveTranslator)
				{
					RuntimeLiveQueue.SetUserEnabled(on: true);
					RuntimeLiveQueue.Resume();
				}
				status = "Automatic translation started. Live stays on for the screen.";
				MLog.Info("[URT] AUTO start. Cap=" + GetSelectedAmount() + " Candidates=" + ((inventory != null && inventory.Candidates != null) ? inventory.Candidates.Count : 0) + " Cache=" + ((cache != null) ? cache.Count : 0) + " Azure+fallbacks parallel");
				RunAutomaticTranslationAsync(autoCancellation.Token);
			}
		}

		private void StopAutomaticTranslation()
		{
			status = "Stopping after current API request...";
			if (autoCancellation != null)
			{
				autoCancellation.Cancel();
			}
			autoRunning = false;
			startTranslateAfterScan = false;
			ClearStuckBatchFlag();
		}

		private void ClearStuckBatchFlag()
		{
			if (batchManager.Progress != null)
			{
				batchManager.Progress.Running = false;
				batchManager.Progress.Finished = true;
			}
		}

		private async void RunAutomaticTranslationAsync(CancellationToken cancellationToken)
		{
			int batches = 0;
			bool completedNormally = false;
			autoFailSweeps = 0;
			await Task.Yield();
			try
			{
				await Task.Run(delegate
				{
					BuildAutoStableQueue();
				}, cancellationToken);
				MLog.Info("[URT] AUTO queue " + ((autoStableQueue != null) ? autoStableQueue.Count : 0) + " of " + ((inventory != null && inventory.Candidates != null) ? inventory.Candidates.Count : 0) + " realPending=" + uiPending);
				status = "AUTO queue " + ((autoStableQueue != null) ? autoStableQueue.Count : 0) + " pending.";
				while (!cancellationToken.IsCancellationRequested)
				{
					List<TranslationCandidate> group = TakeNextFromStableQueue();
					if (group == null || group.Count == 0)
					{
						if (autoFailSweeps < 1 && autoRunFailedCandidates.Count > 0)
						{
							autoFailSweeps++;
							autoRunFailedCandidates.Clear();
							autoRunRetryCounts.Clear();
							MLog.Info("[URT] AUTO b68 retry failed texts. sweep=" + autoFailSweeps);
							await Task.Run(delegate
							{
								BuildAutoStableQueue();
							}, cancellationToken);
							continue;
						}
						int left = ((autoStableQueue != null) ? Math.Max(0, autoStableQueue.Count - autoStablePos) : 0);
						status = "Automatic translation complete. Reason=queue empty. Batches=" + batches + " | leftover in snapshot=" + left + " | skipped=" + autoRunFailedCandidates.Count + " | cache=" + cache.Count;
						MLog.Info("[Universal RimTranslator] TRANSLATE ALL STOPPED reason=queue-empty batches=" + batches + " leftover=" + left);
						completedNormally = true;
						break;
					}
					batches++;
					status = "AUTO batch " + batches + ": " + Math.Min(GetSelectedAmount(), group.Count) + " from " + group[0].SourceLanguage + " -> " + targetLanguage + " | " + selectedMod;
					await RunOneBatch(group, cancellationToken, forceIgnoreCache: true);
					int translatedNow = ((batchManager.Progress != null) ? batchManager.Progress.Translated : 0);
					int failedNow = ((batchManager.Progress != null) ? batchManager.Progress.Failed : 0);
					if (translatedNow == 0 && group.Count > 0)
					{
						int wait = SecondsUntilAnyProviderUsable();
						if (wait > 0)
						{
							autoProviderWaits++;
							autoStableQueue.InsertRange(Math.Min(autoStablePos, autoStableQueue.Count), group);
							if (autoProviderWaits > 3)
							{
								MLog.Info("[URT] AUTO paused: no provider available for a long time. Texts were NOT marked as failed.");
								status = "Automatic translation paused: all providers are unavailable (limits/cooldown). Nothing was marked as failed - start AUTO again later.";
								break;
							}
							int sleep = Math.Max(5, Math.Min(wait, 600));
							MLog.Info("[URT] AUTO: all providers paused; waiting " + sleep + "s. Texts are not counted as failed.");
							status = "All providers paused (limits/cooldown). Waiting " + sleep + " s...";
							try
							{
								await Task.Delay(TimeSpan.FromSeconds(sleep), cancellationToken);
							}
							catch (OperationCanceledException)
							{
								break;
							}
							continue;
						}
					}
					if (translatedNow > 0)
					{
						autoProviderWaits = 0;
					}
					RemoveFinishedCandidates(group);
					int skippedNow = MarkCurrentAutoBatchFailures(group);
					int retryNow = RequeueAutoBatchFailures(group);
					int permanentlySkippedNow = Math.Max(0, skippedNow - retryNow);
					if (translatedNow == 0 && failedNow == 0 && group.Count > 0)
					{
						MLog.Info("[URT] AUTO batch translated nothing and failed nothing. Untranslated entries were hidden from the queue; stopping to avoid repeated requests.");
						status = "Automatic translation stopped: no usable provider response. Hidden after failure: " + skippedNow + ". Remaining: " + uiPending + ".";
						break;
					}
					if (batchManager.Progress != null)
					{
						RequestPendingRecount();
						uiCandidates = ((uiScanCandidates > 0) ? uiScanCandidates : inventory.Candidates.Count);
						uiCountStamp = Environment.TickCount;
					}
					MLog.Info("[URT] Batch done. Translated=" + ((batchManager.Progress != null) ? batchManager.Progress.Translated : 0) + " Failed=" + ((batchManager.Progress != null) ? batchManager.Progress.Failed : 0) + " Cache=" + cache.Count + " Pending=" + uiPending);
					autoBatchesSinceSave++;
					if (autoBatchesSinceSave >= 1)
					{
						autoBatchesSinceSave = 0;
						SaveCache(refreshLive: false);
					}
					if (cancellationToken.IsCancellationRequested)
					{
						status = "Automatic translation stopped. Cache: " + cache.Count;
						break;
					}
					if (retryNow > 0 || skippedNow > 0)
					{
						status = "AUTO batch " + batches + " retrying " + retryNow + " failed entries; permanently skipped " + permanentlySkippedNow + ". Cache: " + cache.Count;
						MLog.Info("[URT] AUTO retry queue added=" + retryNow + " permanentlySkipped=" + permanentlySkippedNow + " maxAttempts=" + 3 + " remaining=" + uiPending);
					}
					await Task.Yield();
				}
			}
			catch (Exception ex3)
			{
				Exception ex = ex3;
				status = "AUTOMATIC TRANSLATION ERROR: " + ex.GetType().Name + ": " + ex.Message;
			}
			finally
			{
				SaveCache();
				autoRunning = false;
				liveLockedOff = false;
				if (completedNormally)
				{
					int stillListed = ((inventory != null && inventory.Candidates != null) ? inventory.Candidates.Count : 0);
					startTranslateAfterScan = false;
					enableLiveTranslator = true;
					RuntimeLiveQueue.Resume();
					RuntimeLiveQueue.SetUserEnabled(on: true);
					MLog.Info("[Universal RimTranslator] AUTO finished. No rescan. leftover=" + stillListed);
					status = "Translate All finished. Another scan will not start. Remaining: " + stillListed + ".";
				}
				else if (enableLiveTranslator)
				{
					RuntimeLiveQueue.Resume();
					RuntimeLiveQueue.SetUserEnabled(on: true);
					MLog.Info("[Universal RimTranslator] Automatic translation stopped. Live stays as the player left it.");
				}
				if (autoCancellation != null)
				{
					autoCancellation.Dispose();
					autoCancellation = null;
				}
			}
		}

		private async Task RunOneBatch(IList<TranslationCandidate> group, CancellationToken cancellationToken, bool forceIgnoreCache = false)
		{
			TranslationProviderSettings providerSettings = BuildProviderSettings();
			string batchTarget = LanguageCodeMapper.Canonical(targetLanguage);
			int batchAmount = GetSelectedAmount();
			await Task.Run(() => batchManager.TranslateNextBatchAsync(group, cache, provider, fallbackProvider, deepLApiKey, googleApiKey, batchTarget, batchAmount, cancellationToken, providerSettings, myMemoryProvider, simplyTranslateProvider, yandexProvider, forceIgnoreCache));
		}

		private string AutoCandidateId(TranslationCandidate candidate)
		{
			if (candidate == null)
			{
				return "";
			}
			return candidate.StableIdentityForTarget(targetLanguage);
		}

		private bool IsAutoCandidateFailed(TranslationCandidate candidate)
		{
			if (candidate == null)
			{
				return false;
			}
			if (autoRunFailedCandidates.IsEmpty)
			{
				return false;
			}
			string key = AutoCandidateId(candidate);
			string value;
			return autoRunFailedCandidates.TryGetValue(key, out value) && string.Equals(value, UrtOptEngine.Fingerprint(candidate.SourceText, targetLanguage), StringComparison.OrdinalIgnoreCase);
		}

		private int MarkCurrentAutoBatchFailures(IList<TranslationCandidate> group)
		{
			if (group == null || group.Count == 0)
			{
				return 0;
			}
			int num = 0;
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			for (int i = 0; i < group.Count; i++)
			{
				TranslationCandidate translationCandidate = group[i];
				if (translationCandidate == null)
				{
					continue;
				}
				string key = AutoCandidateId(translationCandidate);
				if (IsReallyCached(translationCandidate, target))
				{
					autoRunFailedCandidates.TryRemove(key, out var _);
					continue;
				}
				string text = UrtOptEngine.Fingerprint(translationCandidate.SourceText, target);
				if (!autoRunFailedCandidates.TryGetValue(key, out var value2))
				{
					if (autoRunFailedCandidates.TryAdd(key, text))
					{
						num++;
					}
				}
				else if (!string.Equals(value2, text, StringComparison.OrdinalIgnoreCase))
				{
					autoRunFailedCandidates[key] = text;
					num++;
				}
			}
			return num;
		}

		private int RequeueAutoBatchFailures(IList<TranslationCandidate> group)
		{
			if (group == null || group.Count == 0 || autoStableQueue == null)
			{
				return 0;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			int num = 0;
			for (int i = 0; i < group.Count; i++)
			{
				TranslationCandidate translationCandidate = group[i];
				if (translationCandidate == null || IsReallyCached(translationCandidate, target))
				{
					continue;
				}
				string text = AutoCandidateId(translationCandidate);
				if (!string.IsNullOrEmpty(text))
				{
					autoRunRetryCounts.TryGetValue(text, out var value);
					if (value < 3)
					{
						autoRunFailedCandidates.TryRemove(text, out var _);
						autoRunRetryCounts[text] = value + 1;
						autoStableQueue.Add(translationCandidate);
						num++;
					}
				}
			}
			return num;
		}

		private bool HasAnyPendingWork()
		{
			if (inventory == null || inventory.Candidates == null)
			{
				return false;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			string b = "(All mods)";
			for (int i = 0; i < inventory.Candidates.Count; i++)
			{
				TranslationCandidate translationCandidate = inventory.Candidates[i];
				if (translationCandidate != null && !string.IsNullOrWhiteSpace(translationCandidate.SourceText) && (string.Equals(selectedMod, b, StringComparison.OrdinalIgnoreCase) || string.Equals(translationCandidate.ModName, selectedMod, StringComparison.OrdinalIgnoreCase)) && !IsAutoCandidateFailed(translationCandidate) && !IsReallyCached(translationCandidate, target))
				{
					return true;
				}
			}
			return false;
		}

		private bool CandidateLanguageOk(TranslationCandidate c)
		{
			if (c == null)
			{
				return false;
			}
			if (selectedSourceLanguages == null || selectedSourceLanguages.Count == 0)
			{
				return true;
			}
			if (SourceLanguageSelected(c.SourceLanguage, selectedSourceLanguages))
			{
				return true;
			}
			string text = (c.FilePath ?? "").Replace('\\', '/');
			bool flag = text.IndexOf("/Languages/", StringComparison.OrdinalIgnoreCase) >= 0 || text.StartsWith("Languages/", StringComparison.OrdinalIgnoreCase);
			return !flag;
		}

		private static bool SourceLanguageSelected(string candidateLang, List<string> selected)
		{
			if (selected == null || selected.Count == 0)
			{
				return true;
			}
			string text = LanguageCodeMapper.Canonical(candidateLang ?? "");
			string text2 = text;
			int num = text.IndexOf('(');
			if (num > 1)
			{
				text2 = text.Substring(0, num).Trim();
			}
			int num2 = text2.IndexOf(' ');
			if (num2 > 1)
			{
				text2 = text2.Substring(0, num2);
			}
			for (int i = 0; i < selected.Count; i++)
			{
				string text3 = LanguageCodeMapper.Canonical(selected[i] ?? "");
				if (text3.Length != 0)
				{
					if (string.Equals(text3, text, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					if (string.Equals(text3, text2, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					if (text.StartsWith(text3, StringComparison.OrdinalIgnoreCase) && (text.Length == text3.Length || text[text3.Length] == ' ' || text[text3.Length] == '('))
					{
						return true;
					}
				}
			}
			return false;
		}

		private int SecondsUntilAnyProviderUsable()
		{
			TranslationProviderSettings translationProviderSettings = BuildProviderSettings();
			List<string> list = new List<string>();
			if (translationProviderSettings.EnableAzure && !string.IsNullOrWhiteSpace(translationProviderSettings.AzureApiKey))
			{
				list.Add("Azure Translator");
			}
			if (translationProviderSettings.EnableMicrosoftEdge)
			{
				list.Add("Microsoft Edge");
			}
			if (translationProviderSettings.EnableDeepL && !string.IsNullOrWhiteSpace(translationProviderSettings.DeepLApiKey))
			{
				list.Add("DeepL");
			}
			if (translationProviderSettings.EnableGoogleCloud && !string.IsNullOrWhiteSpace(translationProviderSettings.GoogleCloudApiKey))
			{
				list.Add("Google Cloud Translation");
			}
			if (translationProviderSettings.EnableYandex)
			{
				list.Add("Yandex");
			}
			if (translationProviderSettings.EnableGoogleWeb)
			{
				list.Add("Google web");
			}
			if (translationProviderSettings.EnableSimplyTranslate)
			{
				list.Add("SimplyTranslate");
			}
			if (translationProviderSettings.EnableMyMemory)
			{
				list.Add("MyMemory");
			}
			if (translationProviderSettings.EnableLingva)
			{
				list.Add("Lingva");
			}
			if (translationProviderSettings.EnableGemini && !string.IsNullOrWhiteSpace(translationProviderSettings.GeminiApiKey))
			{
				list.Add("Gemini");
			}
			if (translationProviderSettings.EnableCustomEndpoint)
			{
				list.Add("Custom endpoint");
			}
			if (list.Count == 0)
			{
				return 0;
			}
			int num = int.MaxValue;
			foreach (string item in list)
			{
				int num2 = UrtOptEngine.SecondsUntilUsable(item);
				if (num2 <= 0)
				{
					return 0;
				}
				if (num2 < num)
				{
					num = num2;
				}
			}
			return (num != int.MaxValue) ? num : 0;
		}

		private void BuildAutoStableQueue()
		{
			autoStableQueue = new List<TranslationCandidate>();
			autoStablePos = 0;
			autoAppliedFromSource = 0;
			if (inventory == null || inventory.Candidates == null)
			{
				return;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			string b = "(All mods)";
			for (int i = 0; i < inventory.Candidates.Count; i++)
			{
				TranslationCandidate translationCandidate = inventory.Candidates[i];
				if (translationCandidate != null && !string.IsNullOrWhiteSpace(translationCandidate.SourceText) && translationCandidate.SourceText.Trim().Length >= 3 && (!LooksMostlyTargetSource(translationCandidate.SourceText) || LanguageDetector.HasResidualEnglish(translationCandidate.SourceText, target) || LanguageDetector.IsHalfTranslated(translationCandidate.SourceText, target)) && !(translationCandidate.ModPackageId ?? "").StartsWith("Ludeon.", StringComparison.OrdinalIgnoreCase) && (string.Equals(selectedMod, b, StringComparison.OrdinalIgnoreCase) || string.Equals(translationCandidate.ModName, selectedMod, StringComparison.OrdinalIgnoreCase)) && CandidateLanguageOk(translationCandidate) && !IsAutoCandidateFailed(translationCandidate) && !IsReallyCached(translationCandidate, target))
				{
					autoStableQueue.Add(translationCandidate);
				}
			}
			UrtLivePrewarmPriority.Sort(autoStableQueue);
			MLog.Info("[URT] AUTO b70-restore queue " + autoStableQueue.Count + " of " + inventory.Candidates.Count + ". Live takes halves. Failed texts return once.");
		}

		private bool TryApplyExistingSourceTranslation(TranslationCandidate c, string target)
		{
			if (c == null || cache == null || string.IsNullOrWhiteSpace(c.SourceText))
			{
				return false;
			}
			if (!cache.TryGetBySource(c.SourceText, target, out var translation))
			{
				return false;
			}
			if (string.IsNullOrWhiteSpace(translation) || string.Equals(translation.Trim(), c.SourceText.Trim(), StringComparison.Ordinal))
			{
				return false;
			}
			cache.Put(new CachedTranslation
			{
				ModName = c.ModName,
				ModPackageId = c.ModPackageId,
				Type = c.Type,
				DefType = c.DefType,
				Key = c.Key,
				SourceLanguage = c.SourceLanguage,
				SourceText = c.SourceText,
				OriginalSourceText = c.SourceText,
				TargetLanguage = target,
				Translation = translation,
				AutomaticTranslation = translation,
				FilePath = c.FilePath
			});
			autoAppliedFromSource++;
			return true;
		}

		private List<TranslationCandidate> TakeNextFromStableQueue()
		{
			if (autoStableQueue == null)
			{
				return null;
			}
			int selectedAmount = GetSelectedAmount();
			List<TranslationCandidate> list = new List<TranslationCandidate>(selectedAmount);
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			while (autoStablePos < autoStableQueue.Count && list.Count < selectedAmount)
			{
				TranslationCandidate translationCandidate = autoStableQueue[autoStablePos++];
				if (translationCandidate != null && !IsAutoCandidateFailed(translationCandidate) && !IsReallyCached(translationCandidate, target))
				{
					list.Add(translationCandidate);
				}
			}
			return (list.Count == 0) ? null : list;
		}

		private List<TranslationCandidate> GetNextSourceLanguageGroup()
		{
			int selectedAmount = GetSelectedAmount();
			List<TranslationCandidate> list = new List<TranslationCandidate>(selectedAmount);
			if (inventory == null || inventory.Candidates == null)
			{
				return null;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			string b = "(All mods)";
			int i = autoCandidateIndex;
			if (i < 0)
			{
				i = 0;
			}
			for (; i < inventory.Candidates.Count; i++)
			{
				if (list.Count >= selectedAmount)
				{
					break;
				}
				TranslationCandidate translationCandidate = inventory.Candidates[i];
				if (translationCandidate != null && !string.IsNullOrWhiteSpace(translationCandidate.SourceText) && translationCandidate.SourceText.Trim().Length >= 3 && (string.Equals(selectedMod, b, StringComparison.OrdinalIgnoreCase) || string.Equals(translationCandidate.ModName, selectedMod, StringComparison.OrdinalIgnoreCase)) && CandidateLanguageOk(translationCandidate) && (!autoRunning || !IsAutoCandidateFailed(translationCandidate)) && !IsReallyCached(translationCandidate, target))
				{
					list.Add(translationCandidate);
				}
			}
			autoCandidateIndex = i;
			if (list.Count == 0 && !autoCursorWrapped && inventory.Candidates.Count > 0)
			{
				autoCursorWrapped = true;
				autoCandidateIndex = 0;
				return GetNextSourceLanguageGroup();
			}
			return (list.Count == 0) ? null : list;
		}

		private int GetSelectedAmount()
		{
			if (!int.TryParse(batchAmountText, out var result))
			{
				result = 512;
			}
			return Math.Max(1, Math.Min(20000, result));
		}

		private void DropBatchFromInventory(List<TranslationCandidate> group)
		{
			if (group == null || inventory == null || inventory.Candidates == null)
			{
				return;
			}
			HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < group.Count; i++)
			{
				TranslationCandidate translationCandidate = group[i];
				if (translationCandidate != null)
				{
					keys.Add(translationCandidate.ModPackageId + "\n" + translationCandidate.Type + "\n" + translationCandidate.DefType + "\n" + translationCandidate.Key + "\n" + translationCandidate.SourceText);
				}
			}
			if (keys.Count != 0)
			{
				inventory.Candidates.RemoveAll((TranslationCandidate c) => c != null && keys.Contains(c.ModPackageId + "\n" + c.Type + "\n" + c.DefType + "\n" + c.Key + "\n" + c.SourceText));
				autoCandidateIndex = 0;
			}
		}

		private void RemoveFinishedCandidates(List<TranslationCandidate> group)
		{
			if (group == null || inventory == null || inventory.Candidates == null)
			{
				return;
			}
			string target = LanguageCodeMapper.Canonical(targetLanguage);
			HashSet<string> done = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < group.Count; i++)
			{
				TranslationCandidate translationCandidate = group[i];
				if (translationCandidate != null && IsReallyCached(translationCandidate, target))
				{
					done.Add(translationCandidate.ModPackageId + "\n" + translationCandidate.Type + "\n" + translationCandidate.DefType + "\n" + translationCandidate.Key + "\n" + translationCandidate.SourceText);
				}
			}
			if (done.Count != 0)
			{
				inventory.Candidates.RemoveAll((TranslationCandidate c) => c != null && done.Contains(c.ModPackageId + "\n" + c.Type + "\n" + c.DefType + "\n" + c.Key + "\n" + c.SourceText));
				autoCandidateIndex = 0;
			}
		}

		private bool IsReallyCached(TranslationCandidate c, string target)
		{
			if (c == null || cache == null)
			{
				return false;
			}
			if (cache.TryGet(c.ModPackageId, c.Type, c.DefType, c.Key, c.SourceLanguage, target, out var value) && TranslationCache.IsUsableTranslation(value, c.SourceText))
			{
				return true;
			}
			if (cache.TryGet(c.ModPackageId, c.Type, c.DefType, c.Key, target, out value) && TranslationCache.IsUsableTranslation(value, c.SourceText))
			{
				return true;
			}
			if (!string.IsNullOrWhiteSpace(c.SourceText) && UrtOptEngine.TryRam(c.SourceText, target, out var translation) && TranslationCache.IsGoodStored(c.SourceText, translation, target))
			{
				return true;
			}
			return false;
		}

		public override void ExposeData()
		{
			Scribe_Values.Look(ref deepLApiKey, "deepLApiKey", "");
			Scribe_Values.Look(ref googleApiKey, "googleApiKey", "");
			Scribe_Values.Look(ref myMemoryEndpoint, "myMemoryEndpoint", "https://api.mymemory.translated.net/get");
			Scribe_Values.Look(ref myMemoryEmail, "myMemoryEmail", "");
			Scribe_Values.Look(ref simplyTranslateEndpoint, "simplyTranslateEndpoint", "https://api.simplytranslate.ai/translate");
			Scribe_Values.Look(ref yandexEndpoint, "yandexEndpoint", "https://browser.translate.yandex.net/api/v1/tr.json/translate");
			Scribe_Values.Look(ref enableDeepL, "enableDeepL", defaultValue: false);
			Scribe_Values.Look(ref enableGoogleCloud, "enableGoogleCloud", defaultValue: true);
			Scribe_Values.Look(ref enableMyMemory, "enableMyMemory", defaultValue: true);
			Scribe_Values.Look(ref enableSimplyTranslate, "enableSimplyTranslate", defaultValue: false);
			Scribe_Values.Look(ref enableYandex, "enableYandex", defaultValue: true);
			Scribe_Values.Look(ref enableGoogleWeb, "enableGoogleWeb", defaultValue: false);
			Scribe_Values.Look(ref enableLingva, "enableLingva", defaultValue: false);
			Scribe_Values.Look(ref enableGemini, "enableGemini", defaultValue: false);
			Scribe_Values.Look(ref geminiApiKey, "geminiApiKey", "");
			Scribe_Values.Look(ref enableAzure, "enableAzure", defaultValue: true);
			Scribe_Values.Look(ref enableLiveTranslator, "enableLiveTranslator", defaultValue: true);
			Scribe_Values.Look(ref useLiveV2, "useLiveV2", defaultValue: true);
			Scribe_Values.Look(ref writeDefsAtStart, "writeDefsAtStart", defaultValue: true);
			WriteDefsAtStart = writeDefsAtStart;
			UrtLive2.Engaged = useLiveV2;
			Scribe_Values.Look(ref azureApiKey, "azureApiKey", "");
			Scribe_Values.Look(ref azureRegion, "azureRegion", "global");
			Scribe_Values.Look(ref azureEndpoint, "azureEndpoint", "");
			Scribe_Values.Look(ref enableMicrosoftEdge, "enableMicrosoftEdge", defaultValue: true);
			Scribe_Values.Look(ref enableCustomEndpoint, "enableCustomEndpoint", defaultValue: false);
			Scribe_Values.Look(ref customEndpointType, "customEndpointType", "LibreTranslate");
			Scribe_Values.Look(ref customEndpointUrl, "customEndpointUrl", "");
			Scribe_Values.Look(ref customEndpointModel, "customEndpointModel", "");
			Scribe_Values.Look(ref customEndpointKey, "customEndpointKey", "");
			Scribe_Values.Look(ref wideCoverage, "wideCoverage", defaultValue: false);
			Scribe_Values.Look(ref ModsScanned, "modsScanned", 0);
			Scribe_Values.Look(ref ModsWithLanguages, "modsWithLanguages", 0);
			Scribe_Values.Look(ref ModsWithTranslatableText, "modsWithTranslatableText", 0);
			Scribe_Values.Look(ref LanguageFolders, "languageFolders", 0);
			Scribe_Values.Look(ref FilesScanned, "filesScanned", 0);
			Scribe_Values.Look(ref EntriesExtracted, "entriesExtracted", 0);
			Scribe_Collections.Look(ref Languages, "languages", LookMode.Value);
			Scribe_Collections.Look(ref selectedSourceLanguages, "selectedSourceLanguages", LookMode.Value);
			Scribe_Values.Look(ref targetLanguage, "targetLanguage", "");
			Scribe_Values.Look(ref targetLanguageChosen, "targetLanguageChosen", defaultValue: false);
			Scribe_Values.Look(ref sourceLanguagesChosen, "sourceLanguagesChosen", defaultValue: false);
			Scribe_Values.Look(ref autoMainLanguage, "autoMainLanguage", defaultValue: true);
			Scribe_Values.Look(ref batchAmountText, "batchAmountText", "5000");
			if (Languages == null)
			{
				Languages = new List<string>();
			}
			if (selectedSourceLanguages == null)
			{
				selectedSourceLanguages = new List<string> { "English" };
			}
			if (Scribe.mode == LoadSaveMode.LoadingVars)
			{
				if (string.IsNullOrWhiteSpace(targetLanguage))
				{
					targetLanguage = ActiveGameLanguage();
				}
				targetLanguage = LanguageCodeMapper.Canonical(targetLanguage);
				if (string.IsNullOrWhiteSpace(targetLanguage))
				{
					targetLanguage = "English";
				}
				RemoveTargetFromSources();
			}
		}

		private void PersistChoices()
		{
			try
			{
				LoadedModManager.GetMod<UniversalRimTranslatorMod>()?.WriteSettings();
			}
			catch
			{
			}
		}

		public static string PlayerTargetLanguage()
		{
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod != null && mod.settings != null && !string.IsNullOrWhiteSpace(mod.settings.targetLanguage))
				{
					return LanguageCodeMapper.Canonical(mod.settings.targetLanguage);
				}
			}
			catch
			{
			}
			return "English";
		}

		private static string ActiveGameLanguage()
		{
			try
			{
				LoadedLanguage activeLanguage = LanguageDatabase.activeLanguage;
				if (activeLanguage != null && !string.IsNullOrWhiteSpace(activeLanguage.folderName))
				{
					string text = LanguageCodeMapper.Canonical(activeLanguage.folderName);
					if (!string.IsNullOrWhiteSpace(text))
					{
						return text;
					}
				}
			}
			catch
			{
			}
			return "English";
		}
	}
	public sealed class UniversalRimTranslatorUiText
	{
		public readonly string Key;

		public readonly string English;

		public UniversalRimTranslatorUiText(string key, string english)
		{
			Key = key;
			English = english;
		}
	}
	public static class UniversalRimTranslatorUiCatalog
	{
		public const string PackageId = "misak.universalrimtranslator";

		public static readonly UniversalRimTranslatorUiText[] All = new UniversalRimTranslatorUiText[101]
		{
			new UniversalRimTranslatorUiText("URT_UI_TAB_TRANSLATION", "Translation"),
			new UniversalRimTranslatorUiText("URT_UI_TAB_MODS", "Mods"),
			new UniversalRimTranslatorUiText("URT_UI_TAB_EDITOR", "Editor"),
			new UniversalRimTranslatorUiText("URT_UI_TAB_PROVIDERS", "Providers"),
			new UniversalRimTranslatorUiText("URT_UI_TAB_CACHE", "Cache"),
			new UniversalRimTranslatorUiText("URT_UI_CREATOR", "CREATOR - Bolek98"),
			new UniversalRimTranslatorUiText("URT_UI_STEAM_PROFILE", "STEAM PROFILE"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATION_SETTINGS", "TRANSLATION SETTINGS"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATION_SETTINGS_DESC", "Configure the source, target and translation scope."),
			new UniversalRimTranslatorUiText("URT_UI_TARGET_LANGUAGE", "TARGET LANGUAGE"),
			new UniversalRimTranslatorUiText("URT_UI_SOURCE_LANGUAGES", "SOURCE LANGUAGES"),
			new UniversalRimTranslatorUiText("URT_UI_SELECT_SOURCE_LANGUAGES", "Select source languages below."),
			new UniversalRimTranslatorUiText("URT_UI_ALL_WORKSHOP_MODS", "ALL WORKSHOP MODS"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATION_SCOPE", "TRANSLATION SCOPE"),
			new UniversalRimTranslatorUiText("URT_UI_SCOPE_EVERY_MOD", "Scope: every detected Workshop mod"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATION_CONTROL", "TRANSLATION CONTROL"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATION_CONTROL_DESC", "Step 1: scan. Step 2: translate."),
			new UniversalRimTranslatorUiText("URT_UI_SCAN_WORKSHOP", "SCAN WORKSHOP MODS"),
			new UniversalRimTranslatorUiText("URT_UI_SCAN_STEP1", "1.  SCAN MODS"),
			new UniversalRimTranslatorUiText("URT_UI_SCAN_AUTO_LANG", "languages: AUTO"),
			new UniversalRimTranslatorUiText("URT_UI_SCAN_MANUAL_LANG", "languages"),
			new UniversalRimTranslatorUiText("URT_UI_TEXTS_PER_RUN", "TEXTS PER RUN"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATE_SELECTED_AMOUNT", "2.  TRANSLATE SELECTED AMOUNT"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATE_ALL_MISSING", "2.  TRANSLATE ALL MISSING"),
			new UniversalRimTranslatorUiText("URT_UI_STOP_AUTOMATIC", "STOP AUTOMATIC TRANSLATION"),
			new UniversalRimTranslatorUiText("URT_UI_SELECT_ALL", "SELECT ALL"),
			new UniversalRimTranslatorUiText("URT_UI_CLEAR_ALL", "CLEAR ALL"),
			new UniversalRimTranslatorUiText("URT_UI_MOD_SELECTION", "MOD SELECTION"),
			new UniversalRimTranslatorUiText("URT_UI_MOD_SELECTION_DESC", "Choose whether to translate everything or only one Workshop mod."),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATE_ALL_MODS", "TRANSLATE ALL MODS"),
			new UniversalRimTranslatorUiText("URT_UI_RETRANSLATE_NO_TARGET", "RETRANSLATE MODS WITHOUT TARGET LANGUAGE"),
			new UniversalRimTranslatorUiText("URT_UI_TRANSLATE_OUR_MOD", "TRANSLATE UNIVERSAL RIMTRANSLATOR TOO"),
			new UniversalRimTranslatorUiText("URT_UI_RETRANSLATE_SELECTED", "RETRANSLATE SELECTED MOD (IGNORE CACHE)"),
			new UniversalRimTranslatorUiText("URT_UI_RETRY_FAILED", "RETRY UNTRANSLATED / FAILED TEXTS"),
			new UniversalRimTranslatorUiText("URT_UI_OPEN_MOD_EDITOR", "OPEN ALL TEXTS OF THIS MOD IN EDITOR"),
			new UniversalRimTranslatorUiText("URT_UI_SEARCH_MODS", "Search mods by name:"),
			new UniversalRimTranslatorUiText("URT_UI_PREV", "PREV"),
			new UniversalRimTranslatorUiText("URT_UI_NEXT", "NEXT"),
			new UniversalRimTranslatorUiText("URT_UI_CURRENT_SCOPE_ALL", "Current scope: ALL MODS"),
			new UniversalRimTranslatorUiText("URT_UI_EDITOR", "TRANSLATION EDITOR"),
			new UniversalRimTranslatorUiText("URT_UI_EDITOR_SEARCH", "Search source text or translation."),
			new UniversalRimTranslatorUiText("URT_UI_FIND_MOD", "FIND MOD"),
			new UniversalRimTranslatorUiText("URT_UI_ALL_TEXTS", "ALL TEXTS"),
			new UniversalRimTranslatorUiText("URT_UI_ALL_MODS", "ALL MODS"),
			new UniversalRimTranslatorUiText("URT_UI_SOURCE", "SOURCE"),
			new UniversalRimTranslatorUiText("URT_UI_AUTOMATIC_CURRENT", "AUTOMATIC / CURRENT"),
			new UniversalRimTranslatorUiText("URT_UI_YOUR_TRANSLATION", "YOUR TRANSLATION"),
			new UniversalRimTranslatorUiText("URT_UI_RETRANSLATE_TEXT", "RETRANSLATE TEXT"),
			new UniversalRimTranslatorUiText("URT_UI_RETRANSLATE_MOD", "RETRANSLATE MOD"),
			new UniversalRimTranslatorUiText("URT_UI_SAVE_MANUAL", "SAVE MANUAL"),
			new UniversalRimTranslatorUiText("URT_UI_RESTORE_AUTO", "RESTORE AUTO"),
			new UniversalRimTranslatorUiText("URT_UI_RESTORE_SOURCE", "RESTORE SOURCE"),
			new UniversalRimTranslatorUiText("URT_UI_PROVIDERS", "TRANSLATION PROVIDERS"),
			new UniversalRimTranslatorUiText("URT_UI_PROVIDERS_DESC", "Providers are tried from top to bottom for each text. A failed provider is not globally disabled."),
			new UniversalRimTranslatorUiText("URT_UI_PRIMARY_API", "Primary API translator"),
			new UniversalRimTranslatorUiText("URT_UI_API_FALLBACK", "API fallback"),
			new UniversalRimTranslatorUiText("URT_UI_YANDEX_DESC", "FREE • no key • browser endpoint • higher priority"),
			new UniversalRimTranslatorUiText("URT_UI_SIMPLY_DESC", "FREE • no key • public instance failover"),
			new UniversalRimTranslatorUiText("URT_UI_MEMORY_DESC", "FREE • no key • low-volume fallback"),
			new UniversalRimTranslatorUiText("URT_UI_LIBRE_DESC", "OPTIONAL • public mirrors / custom endpoint"),
			new UniversalRimTranslatorUiText("URT_UI_CACHE_EXPORT", "CACHE / EXPORT"),
			new UniversalRimTranslatorUiText("URT_UI_SAVE_CACHE", "Save translation cache"),
			new UniversalRimTranslatorUiText("URT_UI_DELETE_CACHE", "DELETE ALL TRANSLATIONS / CACHE"),
			new UniversalRimTranslatorUiText("URT_UI_EXPORT_CACHE", "Export cached translations as language mods"),
			new UniversalRimTranslatorUiText("URT_UI_EXPORT_REVIEW", "Export failed texts to review CSV"),
			new UniversalRimTranslatorUiText("URT_UI_IMPORT_REVIEW", "Import reviewed CSV"),
			new UniversalRimTranslatorUiText("URT_UI_RELOAD_PACKS", "Reload local translation memory"),
			new UniversalRimTranslatorUiText("URT_UI_SCAN_STATS", "SCAN STATISTICS"),
			new UniversalRimTranslatorUiText("URT_UI_STATUS", "STATUS"),
			new UniversalRimTranslatorUiText("URT_UI_CURRENT_RUN", "CURRENT RUN"),
			new UniversalRimTranslatorUiText("URT_UI_RUNNING", "RUNNING..."),
			new UniversalRimTranslatorUiText("URT_UI_NOT_TESTED", "Not tested."),
			new UniversalRimTranslatorUiText("URT_UI_READY", "Ready."),
			new UniversalRimTranslatorUiText("URT_UI_NONE", "(none)"),
			new UniversalRimTranslatorUiText("URT_UI_SELECT_TRANSLATION", "Select a translation on the left."),
			new UniversalRimTranslatorUiText("URT_UI_CLOSE", "Close"),
			new UniversalRimTranslatorUiText("URT_UI_DONE", "Done"),
			new UniversalRimTranslatorUiText("URT_UI_DONE_CLOSE", "Done — close list"),
			new UniversalRimTranslatorUiText("URT_UI_SOURCES_SELECTED", "{0} languages selected. Press Scan."),
			new UniversalRimTranslatorUiText("URT_UI_SOURCES_CLEARED", "Sources cleared."),
			new UniversalRimTranslatorUiText("URT_UI_FILES", "Files"),
			new UniversalRimTranslatorUiText("URT_UI_LOADING_EDITOR", "Loading editor..."),
			new UniversalRimTranslatorUiText("URT_UI_KEY", "Key"),
			new UniversalRimTranslatorUiText("URT_UI_TYPE", "Type"),
			new UniversalRimTranslatorUiText("URT_UI_FILE", "File"),
			new UniversalRimTranslatorUiText("URT_UI_FILE_RUNTIME", "File: (runtime / generated)"),
			new UniversalRimTranslatorUiText("URT_UI_LIVE_TITLE", "LIVE TRANSLATOR"),
			new UniversalRimTranslatorUiText("URT_UI_LIVE_ON", "LIVE  [ ON ]  click to turn off"),
			new UniversalRimTranslatorUiText("URT_UI_LIVE_PAUSED", "LIVE  [ PAUSED ]  click to turn off"),
			new UniversalRimTranslatorUiText("URT_UI_LIVE_BUTTON_OFF", "LIVE  [ OFF ]  click to turn on"),
			new UniversalRimTranslatorUiText("URT_UI_LIVE_LOCKED", "Live is locked OFF during Scan / Translate All."),
			new UniversalRimTranslatorUiText("URT_UI_LIVE_ON_HINT", "Live ON: only on-screen text that scan or cache does not have yet."),
			new UniversalRimTranslatorUiText("URT_UI_LIVE_OFF", "Live translator OFF."),
			new UniversalRimTranslatorUiText("URT_UI_DELETE_CACHE_CONFIRM", "Delete the ENTIRE translation cache? This cannot be undone. Use this only when you want to start over."),
			new UniversalRimTranslatorUiText("URT_UI_CLOSE_STUCK", "Close stuck pending"),
			new UniversalRimTranslatorUiText("URT_UI_LOAD_CACHE", "Load translation cache"),
			new UniversalRimTranslatorUiText("URT_UI_OPTIMIZE_CACHE", "Optimize cache"),
			new UniversalRimTranslatorUiText("URT_UI_PURGE_DIRTY_CACHE", "Purge mixed / unfinished (loading screen)"),
			new UniversalRimTranslatorUiText("URT_UI_PENDING", "PENDING"),
			new UniversalRimTranslatorUiText("URT_UI_CANDIDATES", "CANDIDATES"),
			new UniversalRimTranslatorUiText("URT_UI_CACHE_WORD", "CACHE")
		};

		public static Dictionary<string, UniversalRimTranslatorUiText> BuildMap()
		{
			Dictionary<string, UniversalRimTranslatorUiText> dictionary = new Dictionary<string, UniversalRimTranslatorUiText>(StringComparer.Ordinal);
			UniversalRimTranslatorUiText[] all = All;
			foreach (UniversalRimTranslatorUiText universalRimTranslatorUiText in all)
			{
				dictionary[universalRimTranslatorUiText.Key] = universalRimTranslatorUiText;
			}
			return dictionary;
		}
	}
	internal static class UrtApplyQueue
	{
		private static readonly Queue<CachedTranslation> Pending = new Queue<CachedTranslation>();

		private const int PerTick = 12;

		private const double MainThreadBudgetMs = 1.0;

		public static int Count
		{
			get
			{
				lock (Pending)
				{
					return Pending.Count;
				}
			}
		}

		public static void Enqueue(CachedTranslation entry)
		{
			if (entry == null || string.IsNullOrWhiteSpace(entry.Translation))
			{
				return;
			}
			lock (Pending)
			{
				Pending.Enqueue(entry);
			}
		}

		private static int lastPumpFrame = -1;

		public static void Pump()
		{
			// b114: Defs / LanguageDatabase are only touched from the main thread, at most once per frame
			if (!UrtMainThread.IsMain)
			{
				return;
			}
			int frameCount = Time.frameCount;
			if (frameCount == lastPumpFrame)
			{
				return;
			}
			lastPumpFrame = frameCount;
			int i = 0;
			long num = (long)((double)Stopwatch.Frequency * 0.001);
			if (num < 1)
			{
				num = 1L;
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (i < 12 && stopwatch.ElapsedTicks < num)
			{
				CachedTranslation entry;
				lock (Pending)
				{
					if (Pending.Count == 0)
					{
						break;
					}
					entry = Pending.Dequeue();
				}
				i++;
				try
				{
					DefTranslationApplier.ApplyOne(entry);
				}
				catch
				{
				}
				try
				{
					LanguageInjection.InjectOne(entry);
				}
				catch
				{
				}
			}
			if (i > 0)
			{
				DefTranslationApplier.FlushRuleCaches();
			}
		}
	}
	// b114: thread-safe hand-off of Def / LanguageDatabase / RimWorld cache writes to the main thread
	internal static class UrtMainThread
	{
		private static readonly ConcurrentQueue<Action> Actions = new ConcurrentQueue<Action>();

		private static int lastPumpFrame = -1;

		private static int errorsLogged;

		private const int MaxPerFrame = 32;

		private static readonly long BudgetTicks = Math.Max(1L, Stopwatch.Frequency / 500);

		public static bool IsMain
		{
			get
			{
				try
				{
					return UnityData.IsInMainThread;
				}
				catch
				{
					return false;
				}
			}
		}

		public static int Pending => Actions.Count;

		public static void Post(Action action)
		{
			if (action != null)
			{
				Actions.Enqueue(action);
			}
		}

		public static void Pump()
		{
			if (!IsMain)
			{
				return;
			}
			int frameCount = Time.frameCount;
			if (frameCount == lastPumpFrame)
			{
				return;
			}
			bool flag = false;
			try
			{
				flag = LongEventHandler.AnyEventNowOrWaiting;
			}
			catch
			{
			}
			if (flag)
			{
				return;
			}
			lastPumpFrame = frameCount;
			Stopwatch stopwatch = Stopwatch.StartNew();
			int num = 0;
			Action result;
			while (num < MaxPerFrame && stopwatch.ElapsedTicks < BudgetTicks && Actions.TryDequeue(out result))
			{
				num++;
				try
				{
					result();
				}
				catch (Exception ex)
				{
					if (Interlocked.Increment(ref errorsLogged) <= 10)
					{
						MLog.Warn("[URT] main-thread apply failed: " + ex.GetType().Name + " " + ex.Message);
					}
				}
			}
			UrtApplyQueue.Pump();
		}
	}
	public class UrtApplyGameComponent : GameComponent
	{
		public UrtApplyGameComponent(Game game)
		{
		}

		public override void GameComponentUpdate()
		{
			UrtMainThread.Pump();
			UrtApplyQueue.Pump();
			RuntimeLiveQueue.PumpCompleted();
			RuntimeTextTranslator.PumpDeferredVisible();
		}

		public override void ExposeData()
		{
			if (Scribe.mode != LoadSaveMode.Saving)
			{
				return;
			}
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod == null || mod.settings == null)
				{
					return;
				}
				UniversalRimTranslatorSettings settings = mod.settings;
				ThreadPool.QueueUserWorkItem(delegate
				{
					try
					{
						settings.FlushLiveCache(force: true);
					}
					catch
					{
					}
				});
			}
			catch
			{
			}
		}
	}
	public static class UrtHash
	{
		public static string Of(string text)
		{
			if (text == null)
			{
				return "0";
			}
			uint num = 2166136261u;
			string text2 = text.Trim().ToLowerInvariant();
			for (int i = 0; i < text2.Length; i++)
			{
				num ^= text2[i];
				num *= 16777619;
			}
			return num.ToString("x8");
		}

		public static string Of64(string text)
		{
			ulong num = 14695981039346656037uL;
			string text2 = text ?? "";
			for (int i = 0; i < text2.Length; i++)
			{
				num ^= text2[i];
				num *= 1099511628211L;
			}
			return num.ToString("x16");
		}
	}
	public static class UrtGlossary
	{
		private static readonly string[][] BeforePairs = new string[2][]
		{
			new string[2] { "pawn", "colonist" },
			new string[2] { "haul", "haul" }
		};

		private static readonly Dictionary<string, string> AfterExact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "colonist", "kolonista" },
			{ "colonists", "kolonisté" },
			{ "pawn", "kolonista" },
			{ "haul", "odnést" },
			{ "hauling", "odnášení" },
			{ "research", "výzkum" },
			{ "raid", "nájezd" },
			{ "stockpile", "sklad" },
			{ "draft", "povolání do zbraně" },
			{ "undraft", "odvolat zbraně" },
			{ "apparel", "oblečení" },
			{ "weapon", "zbraň" },
			{ "melee", "zblízka" },
			{ "mood", "nálada" },
			{ "recreation", "odpočinek" }
		};

		private static readonly Regex TokenSplit = new Regex("(\\{[^{}\\r\\n]*\\}|\\[[^\\]\\r\\n]*\\]|<[^>\\r\\n]*>)", RegexOptions.Compiled);

		public static string Before(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}
			string text2 = text;
			text2 = ReplaceWord(text2, "lovin", "consensual sex");
			text2 = ReplaceWord(text2, "whoring", "prostitution work");
			text2 = ReplaceWord(text2, "haul", "carry items to storage");
			return ReplaceWord(text2, "drafted", "called to arms");
		}

		public static string After(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}
			string text2 = text;
			if (string.Equals(UniversalRimTranslatorSettings.PlayerTargetLanguage(), "Czech", StringComparison.OrdinalIgnoreCase))
			{
				foreach (KeyValuePair<string, string> item in AfterExact)
				{
					text2 = ReplaceWord(text2, item.Key, item.Value);
				}
			}
			return Polish(text2);
		}

		public static string Polish(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}
			string input = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
			input = Regex.Replace(input, "[ \\t]{2,}", " ");
			string text2 = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			input = ((!string.Equals(text2, "French", StringComparison.OrdinalIgnoreCase)) ? Regex.Replace(input, "[ \\t]+([,.;:!?])", "$1") : Regex.Replace(input, "[ \\t]+([,.])", "$1"));
			if (!IsCjkTarget(text2))
			{
				input = SeparateScripts(input);
			}
			return input;
		}

		private static bool IsCjkTarget(string target)
		{
			return string.Equals(target, "Japanese", StringComparison.OrdinalIgnoreCase) || string.Equals(target, "Korean", StringComparison.OrdinalIgnoreCase) || (target ?? "").StartsWith("Chinese", StringComparison.OrdinalIgnoreCase);
		}

		private static string SeparateScripts(string text)
		{
			if (text.Length < 2)
			{
				return text;
			}
			StringBuilder stringBuilder = new StringBuilder(text.Length + 8);
			for (int i = 0; i < text.Length; i++)
			{
				if (i > 0 && ScriptGap(text[i - 1], text[i]))
				{
					stringBuilder.Append(' ');
				}
				stringBuilder.Append(text[i]);
			}
			return stringBuilder.ToString();
		}

		private static bool ScriptGap(char a, char b)
		{
			if (!char.IsLetter(a) || !char.IsLetter(b))
			{
				return false;
			}
			return IsCjk(a) != IsCjk(b) && (IsCjk(a) || IsCjk(b));
		}

		private static bool IsCjk(char c)
		{
			if (c >= '\u3040' && c <= 'ヿ')
			{
				return true;
			}
			if (c >= '㐀' && c <= '\u9fff')
			{
				return true;
			}
			if (c >= '가' && c <= '\ud7af')
			{
				return true;
			}
			return false;
		}

		private static string ReplaceWord(string text, string en, string clearer)
		{
			if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(en))
			{
				return text;
			}
			string[] array = TokenSplit.Split(text);
			for (int i = 0; i < array.Length; i++)
			{
				if (i % 2 != 1)
				{
					array[i] = Regex.Replace(array[i], "\\b" + Regex.Escape(en) + "\\b", clearer ?? "", RegexOptions.IgnoreCase);
				}
			}
			return string.Concat(array);
		}
	}
	public static class UrtCzechXml
	{
		private static readonly object Gate = new object();

		private static readonly Dictionary<string, string> pendingKeyed = new Dictionary<string, string>(StringComparer.Ordinal);

		private static readonly Dictionary<string, Dictionary<string, string>> pendingDefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

		private static readonly Dictionary<string, Dictionary<string, string>> fileMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

		private static int pendingCount;

		private const int FlushThreshold = 500;

		private static readonly HashSet<string> migrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static string PlayerLang()
		{
			string text = "";
			try
			{
				text = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			}
			catch
			{
			}
			if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "English", StringComparison.OrdinalIgnoreCase))
			{
				return "";
			}
			return text;
		}

		private static string GeneratedRoot(string language)
		{
			string text = Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "Generated", language);
			MigrateFromExport(language, text);
			return text;
		}

		private static void MigrateFromExport(string language, string newRoot)
		{
			if (!migrated.Add(language))
			{
				return;
			}
			try
			{
				string text = Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "Export", language);
				if (Directory.Exists(text) && !Directory.Exists(newRoot))
				{
					string[] files = Directory.GetFiles(text, "URT_*.xml", SearchOption.AllDirectories);
					foreach (string text2 in files)
					{
						string path = text2.Substring(text.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
						string text3 = Path.Combine(newRoot, path);
						Directory.CreateDirectory(Path.GetDirectoryName(text3));
						File.Copy(text2, text3, overwrite: false);
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] Could not migrate generated XML from Export: " + ex.Message);
			}
		}

		public static int RemoveShippedFiles(string modRoot)
		{
			if (string.IsNullOrEmpty(modRoot))
			{
				return 0;
			}
			string path = Path.Combine(modRoot, "Languages");
			if (!Directory.Exists(path))
			{
				return 0;
			}
			int num = 0;
			try
			{
				string[] files = Directory.GetFiles(path, "URT_*.xml", SearchOption.AllDirectories);
				foreach (string path2 in files)
				{
					try
					{
						string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path2);
						if (string.Equals(fileNameWithoutExtension, "URT_Keyed", StringComparison.OrdinalIgnoreCase) || string.Equals(fileNameWithoutExtension, "URT_Live", StringComparison.OrdinalIgnoreCase) || string.Equals(fileNameWithoutExtension, "URT_Generated", StringComparison.OrdinalIgnoreCase))
						{
							File.Delete(path2);
							num++;
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			return num;
		}

		public static void RememberKeyed(string key, string translation)
		{
			if (!SafeName(key) || string.IsNullOrWhiteSpace(translation) || string.IsNullOrEmpty(PlayerLang()))
			{
				return;
			}
			lock (Gate)
			{
				pendingKeyed[key] = translation;
				pendingCount++;
				if (pendingCount >= 500)
				{
					FlushLocked();
				}
			}
		}

		public static void RememberDef(string defType, string key, string translation)
		{
			if (string.IsNullOrWhiteSpace(defType) || !SafeName(key) || string.IsNullOrWhiteSpace(translation) || string.IsNullOrEmpty(PlayerLang()))
			{
				return;
			}
			lock (Gate)
			{
				if (!pendingDefs.TryGetValue(defType, out var value))
				{
					value = new Dictionary<string, string>(StringComparer.Ordinal);
					pendingDefs[defType] = value;
				}
				value[key] = translation;
				pendingCount++;
				if (pendingCount >= 500)
				{
					FlushLocked();
				}
			}
		}

		public static void Flush()
		{
			lock (Gate)
			{
				FlushLocked();
			}
		}

		private static void FlushLocked()
		{
			string text = PlayerLang();
			if (string.IsNullOrEmpty(text))
			{
				pendingKeyed.Clear();
				pendingDefs.Clear();
				pendingCount = 0;
				return;
			}
			try
			{
				string path = GeneratedRoot(text);
				if (pendingKeyed.Count > 0)
				{
					string path2 = Path.Combine(path, "Keyed", "URT_Keyed.xml");
					Dictionary<string, string> dictionary = MapFor(path2);
					if (dictionary != null)
					{
						// b113: rewrite the file only when something really changed
						bool changed = false;
						foreach (KeyValuePair<string, string> item in pendingKeyed)
						{
							string old;
							if (!dictionary.TryGetValue(item.Key, out old) || !string.Equals(old, item.Value, StringComparison.Ordinal))
							{
								dictionary[item.Key] = item.Value;
								changed = true;
							}
						}
						if (changed)
						{
							WriteMap(path2, dictionary);
						}
					}
					pendingKeyed.Clear();
				}
				foreach (KeyValuePair<string, Dictionary<string, string>> pendingDef in pendingDefs)
				{
					string path3 = Path.Combine(path, "DefInjected", SafeFolder(pendingDef.Key), "URT_Generated.xml");
					Dictionary<string, string> dictionary2 = MapFor(path3);
					if (dictionary2 == null)
					{
						continue;
					}
					bool changed2 = false;
					foreach (KeyValuePair<string, string> item2 in pendingDef.Value)
					{
						string old2;
						if (!dictionary2.TryGetValue(item2.Key, out old2) || !string.Equals(old2, item2.Value, StringComparison.Ordinal))
						{
							dictionary2[item2.Key] = item2.Value;
							changed2 = true;
						}
					}
					if (changed2)
					{
						WriteMap(path3, dictionary2);
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] Writing generated language XML failed: " + ex.GetType().Name + " " + ex.Message);
			}
			pendingDefs.Clear();
			pendingCount = 0;
		}

		private static Dictionary<string, string> MapFor(string path)
		{
			if (fileMaps.TryGetValue(path, out var value))
			{
				return value;
			}
			value = new Dictionary<string, string>(StringComparer.Ordinal);
			if (File.Exists(path))
			{
				try
				{
					XmlDocument xmlDocument = new XmlDocument();
					xmlDocument.Load(path);
					if (xmlDocument.DocumentElement != null)
					{
						foreach (XmlNode childNode in xmlDocument.DocumentElement.ChildNodes)
						{
							if (childNode.NodeType == XmlNodeType.Element)
							{
								value[childNode.Name] = childNode.InnerText ?? "";
							}
						}
					}
				}
				catch (Exception ex)
				{
					try
					{
						File.Copy(path, path + ".damaged", overwrite: true);
					}
					catch
					{
					}
					MLog.Warn("[URT] " + Path.GetFileName(path) + " was damaged (" + ex.Message + "); a copy was kept as .damaged.");
				}
			}
			fileMaps[path] = value;
			return value;
		}

		private static void WriteMap(string path, Dictionary<string, string> map)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
			stringBuilder.AppendLine("<LanguageData>");
			foreach (KeyValuePair<string, string> item in map)
			{
				if (SafeName(item.Key))
				{
					stringBuilder.Append("  <").Append(item.Key).Append(">");
					stringBuilder.Append(Escape(item.Value));
					stringBuilder.Append("</").Append(item.Key).AppendLine(">");
				}
			}
			stringBuilder.AppendLine("</LanguageData>");
			string text = path + ".tmp";
			File.WriteAllText(text, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			if (File.Exists(path))
			{
				File.Replace(text, path, null);
			}
			else
			{
				File.Move(text, path);
			}
		}

		private static bool SafeName(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
			{
				return false;
			}
			if (!char.IsLetter(key[0]) && key[0] != '_')
			{
				return false;
			}
			foreach (char c in key)
			{
				if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '-')
				{
					return false;
				}
			}
			return true;
		}

		private static string SafeFolder(string value)
		{
			char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
			char[] array = value.ToCharArray();
			for (int i = 0; i < array.Length; i++)
			{
				if (Array.IndexOf(invalidFileNameChars, array[i]) >= 0)
				{
					array[i] = '_';
				}
			}
			return new string(array);
		}

		private static string Escape(string s)
		{
			StringBuilder stringBuilder = new StringBuilder((s ?? "").Length + 8);
			string text = s ?? "";
			foreach (char c in text)
			{
				switch (c)
				{
				case '&':
					stringBuilder.Append("&amp;");
					continue;
				case '<':
					stringBuilder.Append("&lt;");
					continue;
				case '>':
					stringBuilder.Append("&gt;");
					continue;
				}
				if (XmlConvert.IsXmlChar(c) || char.IsSurrogate(c))
				{
					stringBuilder.Append(c);
				}
			}
			return stringBuilder.ToString();
		}
	}
	internal static class UrtFix
	{
		private static readonly ConcurrentQueue<string> Notes = new ConcurrentQueue<string>();

		private static readonly object FileGate = new object();

		private static HashSet<string> keep;

		private static string KeepPath => Path.Combine(GenFilePaths.ConfigFolderPath, "URT_keep_original.txt");

		public static void Act(string drawn, string shown, bool keepOriginal)
		{
			if (string.IsNullOrWhiteSpace(drawn))
			{
				Note("URT: no text under the mouse.");
				return;
			}
			string a = drawn.Trim();
			string b = (string.IsNullOrWhiteSpace(shown) ? a : shown.Trim());
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					Work(a, b, keepOriginal);
				}
				catch (Exception ex)
				{
					Note("URT: fix failed: " + ex.GetType().Name);
				}
			});
		}

		public static void PumpNotes()
		{
			int num = 0;
			string result;
			while (num < 3 && Notes.TryDequeue(out result))
			{
				num++;
				try
				{
					Messages.Message(result, MessageTypeDefOf.SilentInput, historical: false);
				}
				catch
				{
				}
			}
		}

		public static void Note(string text)
		{
			Notes.Enqueue(text);
			MLog.Info("[URT] " + text);
		}

		private static void Work(string drawn, string shown, bool keepOriginal)
		{
			UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
			if (mod == null || mod.settings == null)
			{
				return;
			}
			int manual;
			List<KeyValuePair<string, string>> list = mod.settings.TakeEntriesForFix(drawn, shown, out manual);
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, string> item in list)
			{
				if (!dictionary.ContainsKey(item.Key))
				{
					dictionary[item.Key] = item.Value;
				}
			}
			if (dictionary.Count == 0 && manual == 0)
			{
				string text = (string.Equals(drawn, shown, StringComparison.Ordinal) ? null : shown);
				dictionary[drawn] = text ?? RuntimeTextTranslator.CurrentDrawTranslation(drawn);
			}
			if (dictionary.Count == 0)
			{
				Note("URT: \"" + Short(shown) + "\" is a hand-made translation; change it in the URT editor.");
				return;
			}
			foreach (KeyValuePair<string, string> item2 in dictionary)
			{
				RuntimeTextTranslator.ForgetForDraw(item2.Key, item2.Value);
				if (keepOriginal)
				{
					AddKeep(item2.Key);
					RuntimeTextTranslator.KeepOriginal(item2.Key);
				}
				else
				{
					UrtLive2.Retranslate(item2.Key, item2.Value);
				}
			}
			string text2 = ((dictionary.Count == 1) ? ("\"" + Short(First(dictionary.Keys)) + "\"") : (dictionary.Count + " texts"));
			Note(keepOriginal ? ("URT: " + text2 + " stays untranslated.") : ("URT: translating " + text2 + " again..."));
			if (manual > 0)
			{
				Note("URT: " + manual + " hand-made translation(s) left as they are.");
			}
		}

		public static HashSet<string> KeepList()
		{
			lock (FileGate)
			{
				if (keep != null)
				{
					return keep;
				}
				keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				try
				{
					if (File.Exists(KeepPath))
					{
						string[] array = File.ReadAllLines(KeepPath);
						foreach (string text in array)
						{
							if (!string.IsNullOrWhiteSpace(text))
							{
								keep.Add(text.Replace("\\n", "\n"));
							}
						}
					}
				}
				catch
				{
				}
				return keep;
			}
		}

		private static void AddKeep(string source)
		{
			lock (FileGate)
			{
				HashSet<string> hashSet = KeepList();
				if (!hashSet.Add(source))
				{
					return;
				}
				try
				{
					File.AppendAllText(KeepPath, source.Replace("\r", "").Replace("\n", "\\n") + Environment.NewLine);
				}
				catch
				{
				}
			}
		}

		private static string First(IEnumerable<string> items)
		{
			using (IEnumerator<string> enumerator = items.GetEnumerator())
			{
				if (enumerator.MoveNext())
				{
					return enumerator.Current;
				}
			}
			return "";
		}

		public static string Short(string text)
		{
			text = (text ?? "").Replace("\n", " ").Replace("\r", "");
			return (text.Length > 60) ? (text.Substring(0, 60) + "…") : text;
		}
	}
	internal sealed class UrtFrameWatch : MonoBehaviour
	{
		public static int HookCalls;

		private static bool created;

		private float last;

		private int lastGc0;

		private int lastGcFull;

		private int lines;

		private float sumMs;

		private float maxMs;

		private int frames;

		private int slowFrames;

		private int hooksTotal;

		private float nextSummary;

		private const int MaxLines = 40;

		public static void Create()
		{
			if (created)
			{
				return;
			}
			created = true;
			try
			{
				GameObject gameObject = new GameObject("URT_FrameWatch");
				UnityEngine.Object.DontDestroyOnLoad(gameObject);
				gameObject.AddComponent<UrtFrameWatch>();
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] frame watch not started: " + ex.Message);
			}
		}

		private void Update()
		{
			try
			{
				UrtMainThread.Pump();
			}
			catch
			{
			}
			float realtimeSinceStartup = Time.realtimeSinceStartup;
			int num = GC.CollectionCount(0);
			int num2 = GC.CollectionCount(GC.MaxGeneration);
			int hookCalls = HookCalls;
			HookCalls = 0;
			if (last <= 0f)
			{
				last = realtimeSinceStartup;
				lastGc0 = num;
				lastGcFull = num2;
				nextSummary = realtimeSinceStartup + 30f;
				return;
			}
			float num3 = (realtimeSinceStartup - last) * 1000f;
			last = realtimeSinceStartup;
			bool flag = false;
			try
			{
				flag = LongEventHandler.AnyEventNowOrWaiting;
			}
			catch
			{
			}
			if (!flag)
			{
				frames++;
				sumMs += num3;
				hooksTotal += hookCalls;
				if (num3 > maxMs)
				{
					maxMs = num3;
				}
				if (num3 >= 150f)
				{
					slowFrames++;
					if (lines < 40)
					{
						lines++;
						MLog.Info("[URT] frame " + num3.ToString("0") + " ms in " + State() + ": gc0+" + (num - lastGc0) + " gcFull+" + (num2 - lastGcFull) + " heap=" + HeapMb() + "MB urtTextHooks=" + hookCalls);
					}
				}
				if (realtimeSinceStartup >= nextSummary && lines < 40)
				{
					lines++;
					MLog.Info("[URT] 30s in " + State() + ": frames=" + frames + " avg=" + ((frames == 0) ? 0f : (sumMs / (float)frames)).ToString("0.0") + "ms max=" + maxMs.ToString("0") + "ms slow(>150ms)=" + slowFrames + " urtTextHooks/frame=" + ((frames != 0) ? (hooksTotal / frames) : 0) + " gcFull=" + num2 + " heap=" + HeapMb() + "MB");
					frames = 0;
					sumMs = 0f;
					maxMs = 0f;
					slowFrames = 0;
					hooksTotal = 0;
					nextSummary = realtimeSinceStartup + 30f;
				}
			}
			lastGc0 = num;
			lastGcFull = num2;
		}

		private static string State()
		{
			try
			{
				return Current.ProgramState.ToString();
			}
			catch
			{
				return "?";
			}
		}

		private static long HeapMb()
		{
			try
			{
				return GC.GetTotalMemory(forceFullCollection: false) / 1048576;
			}
			catch
			{
				return -1L;
			}
		}
	}
	internal static class UrtHitch
	{
		private const double HitchMs = 250.0;

		private const int MaxLines = 25;

		private static long lastFrame;

		private static int lines;

		private static int lastGc0;

		private static int lastGc1;

		private static long lastReg;

		private static readonly object Gate = new object();

		private static StringBuilder pending = new StringBuilder();

		public static void Note(string what, long ticks)
		{
			double num = (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
			if (num < 20.0)
			{
				return;
			}
			string value = Thread.CurrentThread.ManagedThreadId.ToString();
			lock (Gate)
			{
				if (pending.Length < 400)
				{
					pending.Append(' ').Append(what).Append('=')
						.Append(num.ToString("0"))
						.Append("ms@t")
						.Append(value);
				}
			}
		}

		public static void Frame(long drawTicks, long regTicks)
		{
			long timestamp = Stopwatch.GetTimestamp();
			int num = GC.CollectionCount(0);
			int num2 = GC.CollectionCount(GC.MaxGeneration);
			if (lastFrame == 0)
			{
				lastFrame = timestamp;
				lastGc0 = num;
				lastGc1 = num2;
				lastReg = regTicks;
				return;
			}
			double num3 = (double)(timestamp - lastFrame) * 1000.0 / (double)Stopwatch.Frequency;
			string text;
			lock (Gate)
			{
				text = pending.ToString();
				pending.Length = 0;
			}
			if (num3 >= 250.0 && lines < 25)
			{
				lines++;
				double num4 = 1000.0 / (double)Stopwatch.Frequency;
				MLog.Info("[URT] hitch " + num3.ToString("0") + " ms: urtDraw=" + ((double)drawTicks * num4).ToString("0.0") + "ms liveApply=" + ((double)Math.Max(0L, regTicks - lastReg) * num4).ToString("0.0") + "ms gc0+" + (num - lastGc0) + " gcFull+" + (num2 - lastGc1) + " livePending=" + RuntimeLiveQueue.PendingCount + " liveOn=" + RuntimeLiveQueue.Active + ((text.Length > 0) ? (" |" + text) : " | no heavy URT work"));
			}
			lastFrame = timestamp;
			lastGc0 = num;
			lastGc1 = num2;
			lastReg = regTicks;
		}
	}
	internal static class UrtLive2
	{
		private sealed class Item
		{
			public string Source;

			public string Masked;

			public List<string> Tokens;

			public string Soft;

			public string SoftFrom;

			public bool SoftShown;

			public bool Force;

			public string Avoid;

			public int Answers;

			public int Unchanged;
		}

		public static volatile bool Engaged = true;

		private const int MaxQueued = 1500;

		private const int BatchItems = 25;

		private const int BatchChars = 9000;

		private const int CollectMs = 120;

		private const int FastTimeoutMs = 15000;

		private const int FreeTimeoutMs = 6000;

		private const int SlowTimeoutMs = 60000;

		private const int FailRetryMs = 300000;

		private const int MaxApplyPerFrame = 40;

		private const int MaxSamples = 40;

		private static readonly ConcurrentQueue<string> Incoming = new ConcurrentQueue<string>();

		private static readonly ConcurrentQueue<Item> SlowQueue = new ConcurrentQueue<Item>();

		private static readonly ConcurrentQueue<KeyValuePair<string, string>> Done = new ConcurrentQueue<KeyValuePair<string, string>>();

		private static readonly ConcurrentDictionary<string, int> State = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

		private static readonly AutoResetEvent Wake = new AutoResetEvent(initialState: false);

		private static readonly AutoResetEvent WakeSlow = new AutoResetEvent(initialState: false);

		private static int started;

		private static int queued;

		private static int samples;

		private static int translatedTotal;

		private static int slowQueued;

		private const int MaxSlowQueued = 150;

		private static readonly Regex Token = new Regex("(\\{\\{[0-9]+\\}\\}|\\{[0-9A-Za-z_:\\.]+\\}|\\$[A-Za-z_][A-Za-z0-9_\\.]*|\\[\\[[^\\]]+\\]\\]|\\[[^\\]]+\\]|<[^>\\r\\n]+>|\\\\n|\\(\\*[A-Za-z_][^)\\r\\n]*\\)|\\(/[A-Za-z_][A-Za-z0-9_]*\\))", RegexOptions.Compiled);

		private static readonly Regex WordRx = new Regex("\\p{L}{4,}", RegexOptions.Compiled);

		private static readonly ConcurrentDictionary<string, int> BenchedUntil = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

		[ThreadStatic]
		private static bool forcedNow;

		public static bool Active => Engaged && RuntimeLiveQueue.Active;

		public static int PendingCount => Math.Max(0, queued);

		public static bool Enqueue(string text)
		{
			if (!Active || string.IsNullOrEmpty(text))
			{
				return false;
			}
			string text2 = text.Trim();
			if (text2.Length < 2 || text2.Length > 8000)
			{
				return false;
			}
			int tickCount = Environment.TickCount;
			if (State.TryGetValue(text2, out var value))
			{
				if (value == 1 || value == 2)
				{
					return false;
				}
				if (tickCount - value < 0)
				{
					return false;
				}
				if (!State.TryUpdate(text2, 1, value))
				{
					return false;
				}
			}
			else
			{
				if (State.Count > 50000)
				{
					State.Clear();
				}
				if (!State.TryAdd(text2, 1))
				{
					return false;
				}
			}
			if (queued >= 1500)
			{
				State.TryRemove(text2, out value);
				return false;
			}
			Interlocked.Increment(ref queued);
			Incoming.Enqueue(text2);
			EnsureThreads();
			Wake.Set();
			return true;
		}

		public static void Pump()
		{
			if (Done.IsEmpty)
			{
				return;
			}
			int num = 0;
			KeyValuePair<string, string> result;
			while (num < 40 && Done.TryDequeue(out result))
			{
				num++;
				try
				{
					RuntimeTextTranslator.RegisterLiveTrusted(result.Key, result.Value);
				}
				catch
				{
				}
			}
		}

		public static void Retranslate(string source, string oldTranslation)
		{
			if (!string.IsNullOrWhiteSpace(source))
			{
				string text = source.Trim();
				Item item = new Item
				{
					Source = text,
					Tokens = new List<string>(),
					Force = true,
					Avoid = oldTranslation
				};
				List<string> tokens = item.Tokens;
				item.Masked = Token.Replace(text, delegate(Match m)
				{
					tokens.Add(m.Value);
					return "⟦" + (tokens.Count - 1) + "⟧";
				});
				try
				{
					item.Masked = UrtGlossary.Before(item.Masked);
				}
				catch
				{
				}
				State[text] = 1;
				Interlocked.Increment(ref slowQueued);
				SlowQueue.Enqueue(item);
				EnsureThreads();
				WakeSlow.Set();
			}
		}

		public static void Forget()
		{
			State.Clear();
		}

		private static void EnsureThreads()
		{
			if (Interlocked.CompareExchange(ref started, 1, 0) == 0)
			{
				Thread thread = new Thread(FastLoop)
				{
					IsBackground = true,
					Name = "URT-Live2-fast"
				};
				Thread thread2 = new Thread(SlowLoop)
				{
					IsBackground = true,
					Name = "URT-Live2-slow"
				};
				thread.Start();
				thread2.Start();
				MLog.Info("[URT] Live v2 started (fast batch thread + slow provider thread).");
			}
		}

		private static void FastLoop()
		{
			while (true)
			{
				try
				{
					Wake.WaitOne(500);
					if (Incoming.IsEmpty)
					{
						continue;
					}
					Thread.Sleep(120);
					while (!Incoming.IsEmpty)
					{
						List<Item> list = TakeBatch();
						if (list.Count > 0)
						{
							RunFast(list);
						}
					}
				}
				catch (Exception ex)
				{
					UrtLiveDiag.Outcome("v2Error");
					if (samples < 40)
					{
						samples++;
						MLog.Info("[URT] live2 error: " + ex.GetType().Name + " " + ex.Message);
					}
					Thread.Sleep(1000);
				}
			}
		}

		private static List<Item> TakeBatch()
		{
			List<Item> list = new List<Item>(25);
			int num = 0;
			string target = Target();
			string result;
			while (list.Count < 25 && num < 9000 && Incoming.TryDequeue(out result))
			{
				Interlocked.Decrement(ref queued);
				Stopwatch stopwatch = Stopwatch.StartNew();
				Item item = Prepare(result, target);
				UrtLiveDiag.Worker(stopwatch.ElapsedMilliseconds);
				if (item != null)
				{
					list.Add(item);
					num += item.Masked.Length;
				}
			}
			return list;
		}

		private static Item Prepare(string text, string target)
		{
			if (!HasLetter(text) || UrtOptEngine.LooksNonTranslatable(text) || LooksLikePath(text))
			{
				Finish(text, "untranslatable");
				return null;
			}
			if ((UrtPackLibrary.Try(text, "", "", "", "", target, out var translation) || UrtOptEngine.TryRam(text, target, out translation)) && Classify(text, translation, target) == null)
			{
				Complete(text, translation, "cacheHit", soft: false);
				return null;
			}
			if (RuntimeTextTranslator.IsKnownTarget(text))
			{
				Finish(text, "knownTarget");
				return null;
			}
			if (!LanguageDetector.HasResidualForeignScript(text, target) && LanguageDetector.LooksLikeTargetText(text, target))
			{
				Finish(text, "alreadyTarget");
				return null;
			}
			Item item = new Item
			{
				Source = text,
				Tokens = new List<string>()
			};
			List<string> tokens = item.Tokens;
			item.Masked = Token.Replace(text, delegate(Match m)
			{
				tokens.Add(m.Value);
				return "⟦" + (tokens.Count - 1) + "⟧";
			});
			try
			{
				item.Masked = UrtGlossary.Before(item.Masked);
			}
			catch
			{
			}
			return item;
		}

		private static void RunFast(List<Item> batch)
		{
			string target = Target();
			TranslationProviderSettings translationProviderSettings = ProviderSettings();
			if (translationProviderSettings == null)
			{
				return;
			}
			List<Item> list = new List<Item>(batch);
			List<IUniversalTranslationProvider> list2 = FastProviders(translationProviderSettings);
			for (int j = 0; j < list2.Count; j++)
			{
				if (list.Count <= 0)
				{
					break;
				}
				IUniversalTranslationProvider universalTranslationProvider = list2[j];
				if (universalTranslationProvider is MicrosoftEdgeTranslateProvider || universalTranslationProvider is AzureTranslateProvider)
				{
					list = Ask(universalTranslationProvider, list, translationProviderSettings, target, 15000);
				}
				else if (!Benched(universalTranslationProvider.Name))
				{
					List<Item> none = list.FindAll((Item i) => i.Soft == null && i.Answers == 0);
					if (none.Count == 0)
					{
						break;
					}
					List<Item> rest = Ask(universalTranslationProvider, none, translationProviderSettings, target, 6000);
					list = list.FindAll((Item i) => !none.Contains(i) || rest.Contains(i));
				}
			}
			bool flag = SlowProviders(translationProviderSettings).Count > 0;
			foreach (Item item in list)
			{
				if (item.Soft != null && !item.SoftShown)
				{
					item.SoftShown = true;
					Complete(item.Source, item.Soft, "acceptedIncomplete", soft: true);
				}
				bool flag2 = item.Answers > 0 && item.Unchanged == item.Answers;
				if (flag && !flag2 && slowQueued < 150)
				{
					Interlocked.Increment(ref slowQueued);
					SlowQueue.Enqueue(item);
					WakeSlow.Set();
				}
				else
				{
					Finalize(item);
				}
			}
		}

		private static void SlowLoop()
		{
			while (true)
			{
				try
				{
					WakeSlow.WaitOne(1000);
					Item result;
					while (SlowQueue.TryDequeue(out result))
					{
						Interlocked.Decrement(ref slowQueued);
						string target = Target();
						TranslationProviderSettings translationProviderSettings = ProviderSettings();
						List<Item> list = new List<Item> { result };
						if (translationProviderSettings != null)
						{
							List<IUniversalTranslationProvider> list2 = SlowProviders(translationProviderSettings);
							if (result.Force)
							{
								List<IUniversalTranslationProvider> list3 = FastProviders(translationProviderSettings);
								list3.AddRange(list2);
								list2 = list3;
							}
							foreach (IUniversalTranslationProvider item in list2)
							{
								if (list.Count == 0)
								{
									break;
								}
								if (!Benched(item.Name))
								{
									list = Ask(item, list, translationProviderSettings, target, 60000);
								}
							}
						}
						foreach (Item item2 in list)
						{
							Finalize(item2);
						}
					}
				}
				catch
				{
					UrtLiveDiag.Outcome("v2SlowError");
					Thread.Sleep(1000);
				}
			}
		}

		private static List<Item> Ask(IUniversalTranslationProvider provider, List<Item> items, TranslationProviderSettings settings, string target, int timeoutMs)
		{
			List<string> list = new List<string>(items.Count);
			foreach (Item item2 in items)
			{
				list.Add(item2.Masked);
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			TranslationProviderResult translationProviderResult = null;
			try
			{
				using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(timeoutMs);
				translationProviderResult = provider.TranslateBatchAsync(list, "auto", target, settings, cancellationTokenSource.Token).GetAwaiter().GetResult();
			}
			catch
			{
				translationProviderResult = null;
			}
			bool flag = translationProviderResult != null && translationProviderResult.Translations != null && translationProviderResult.Translations.Count > 0;
			int num = translationProviderResult?.StatusCode ?? 0;
			UrtLiveDiag.Http(provider.Name + "*", flag && translationProviderResult.Success, stopwatch.ElapsedMilliseconds, num, timeoutMs);
			if (!flag || !translationProviderResult.Success)
			{
				Bench(provider.Name, (num == 429) ? 120000 : 60000);
			}
			if (!flag)
			{
				return items;
			}
			List<Item> list2 = new List<Item>();
			for (int i = 0; i < items.Count; i++)
			{
				Item item = items[i];
				string text = ((i < translationProviderResult.Translations.Count) ? translationProviderResult.Translations[i] : null);
				if (string.IsNullOrWhiteSpace(text))
				{
					list2.Add(item);
					continue;
				}
				string text2 = Unmask(text, item.Tokens);
				try
				{
					text2 = UrtGlossary.After(text2);
				}
				catch
				{
				}
				text2 = LanguageDetector.RepairMarkup(text2).Trim();
				string text3 = Classify(item.Source, text2, target);
				if (text3 == "foreignLeft")
				{
					string text4 = FixForeignRuns(provider, text2, settings, target);
					if (text4 != null)
					{
						text2 = text4;
						text3 = Classify(item.Source, text2, target);
						if (text3 == null)
						{
							UrtLiveDiag.Outcome("foreignPartsFixed");
						}
					}
				}
				if (text3 == null && item.Force && item.Avoid != null && string.Equals(Collapse(text2), Collapse(item.Avoid), StringComparison.OrdinalIgnoreCase))
				{
					item.Answers++;
					if (item.Soft == null)
					{
						item.Soft = text2;
						item.SoftFrom = provider.Name;
					}
					UrtLiveDiag.Reject("sameAsBefore");
					list2.Add(item);
				}
				else if (text3 == null)
				{
					item.Answers++;
					forcedNow = item.Force;
					Complete(item.Source, text2, item.SoftShown ? "improvedBySlow" : "translated", soft: false);
					if (item.Force)
					{
						UrtFix.Note("URT: \"" + UrtFix.Short(item.Source) + "\" -> \"" + UrtFix.Short(text2) + "\" (" + provider.Name + ")");
					}
				}
				else
				{
					item.Answers++;
					if (text3 == "unchanged")
					{
						item.Unchanged++;
					}
					Sample(text3, provider.Name, item.Source, text2);
					if (text3 == "incomplete" && item.Soft == null)
					{
						item.Soft = text2;
						item.SoftFrom = provider.Name;
					}
					UrtLiveDiag.Reject(text3);
					list2.Add(item);
				}
			}
			return list2;
		}

		private static List<string> ForeignRuns(string text, string target)
		{
			List<string> list = new List<string>();
			int num = 0;
			while (num < text.Length)
			{
				if (!LanguageDetector.IsForeignScriptChar(text[num], target))
				{
					num++;
					continue;
				}
				int num2 = num;
				int num3 = num;
				int num4 = num;
				while (num4 < text.Length)
				{
					char c = text[num4];
					if (LanguageDetector.IsForeignScriptChar(c, target))
					{
						num3 = num4;
						num4++;
						continue;
					}
					if (c == ' ' || c == '(' || c == ')' || c == '（' || c == '）' || c == '・' || c == '·' || c == '-')
					{
						int i;
						for (i = num4 + 1; i < text.Length && !LanguageDetector.IsForeignScriptChar(text[i], target) && (text[i] == ' ' || text[i] == '(' || text[i] == ')' || text[i] == '（' || text[i] == '）'); i++)
						{
						}
						if (i < text.Length && LanguageDetector.IsForeignScriptChar(text[i], target))
						{
							num4 = i;
							continue;
						}
						break;
					}
					break;
				}
				int j;
				for (j = num3 + 1; j < text.Length && char.IsLetterOrDigit(text[j]) && text[j] < '\u0080'; j++)
				{
				}
				string text2 = text.Substring(num2, j - num2).Trim();
				if (text2.Length > 0 && !list.Contains(text2))
				{
					list.Add(text2);
				}
				num = j;
			}
			return list;
		}

		private static string FixForeignRuns(IUniversalTranslationProvider provider, string text, TranslationProviderSettings settings, string target)
		{
			List<string> list = ForeignRuns(text, target);
			if (list.Count == 0 || list.Count > 12)
			{
				return null;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
			List<string> list2 = new List<string>();
			foreach (string item in list)
			{
				string text2 = RuntimeTextTranslator.CurrentDrawTranslation(item);
				if (text2 == null && UrtOptEngine.TryRam(item, target, out var translation) && !string.IsNullOrWhiteSpace(translation))
				{
					text2 = translation;
				}
				if (text2 != null && !LanguageDetector.HasResidualForeignScript(text2, target))
				{
					dictionary[item] = text2.Trim();
				}
				else
				{
					list2.Add(item);
				}
			}
			if (list2.Count > 0)
			{
				TranslationProviderResult translationProviderResult = null;
				try
				{
					using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(15000);
					translationProviderResult = provider.TranslateBatchAsync(list2, "auto", target, settings, cancellationTokenSource.Token).GetAwaiter().GetResult();
				}
				catch
				{
					translationProviderResult = null;
				}
				if (translationProviderResult == null || translationProviderResult.Translations == null)
				{
					return null;
				}
				for (int i = 0; i < list2.Count && i < translationProviderResult.Translations.Count; i++)
				{
					string text3 = (translationProviderResult.Translations[i] ?? "").Trim();
					if (text3.Length == 0 || LanguageDetector.HasResidualForeignScript(text3, target))
					{
						return null;
					}
					dictionary[list2[i]] = text3;
				}
			}
			string text4 = text;
			foreach (KeyValuePair<string, string> item2 in dictionary)
			{
				text4 = text4.Replace(item2.Key, item2.Value);
			}
			return text4;
		}

		private static void Finalize(Item item)
		{
			if (item.Force)
			{
				if (item.Soft != null)
				{
					Complete(item.Source, item.Soft, "retranslateNoBetter", soft: true);
					UrtFix.Note("URT: no better translation found for \"" + UrtFix.Short(item.Source) + "\"; kept \"" + UrtFix.Short(item.Soft) + "\". Edit it in the URT editor.");
				}
				else
				{
					UrtLiveDiag.Outcome("retranslateFailed");
					State[item.Source] = Environment.TickCount + 300000;
					UrtFix.Note("URT: no provider translated \"" + UrtFix.Short(item.Source) + "\". Original shown.");
				}
			}
			else if (item.SoftShown)
			{
				UrtLiveDiag.Outcome("notImproved");
			}
			else if (item.Soft != null)
			{
				Complete(item.Source, item.Soft, "acceptedIncomplete", soft: true);
			}
			else if (item.Answers > 0 && item.Unchanged == item.Answers)
			{
				Finish(item.Source, "keptAsIs");
			}
			else
			{
				UrtLiveDiag.Outcome("failed");
				State[item.Source] = Environment.TickCount + 300000;
			}
		}

		private static string Classify(string source, string dst, string target)
		{
			if (string.IsNullOrWhiteSpace(dst))
			{
				return "empty";
			}
			if (!UrtResultValidator.Accept(source, dst))
			{
				return UrtResultValidator.LooksSpliced(source, dst) ? "spliced" : "tokensOrError";
			}
			if (LanguageDetector.HasBrokenMarkup(dst))
			{
				return "brokenMarkup";
			}
			if (string.Equals(Collapse(source), Collapse(dst), StringComparison.OrdinalIgnoreCase))
			{
				return "unchanged";
			}
			if (LanguageDetector.HasResidualForeignScript(dst, target))
			{
				return "foreignLeft";
			}
			if (MostWordsKept(source, dst))
			{
				return "incomplete";
			}
			return null;
		}

		private static bool MostWordsKept(string source, string dst)
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (Match item in WordRx.Matches(Token.Replace(source, " ")))
			{
				hashSet.Add(item.Value);
			}
			if (hashSet.Count < 3)
			{
				return false;
			}
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (Match item2 in WordRx.Matches(Token.Replace(dst, " ")))
			{
				if (hashSet.Contains(item2.Value))
				{
					hashSet2.Add(item2.Value);
				}
			}
			return hashSet2.Count * 2 >= hashSet.Count;
		}

		private static void Complete(string source, string translation, string outcome, bool soft)
		{
			UrtLiveDiag.Outcome(outcome);
			State[source] = 2;
			Done.Enqueue(new KeyValuePair<string, string>(source, translation));
			int num = Interlocked.Increment(ref translatedTotal);
			if (num % 25 == 0)
			{
				MLog.Info("[URT] Live v2 translated " + num + ".");
			}
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				if (mod != null && mod.settings != null)
				{
					bool flag = outcome == "retranslateNoBetter" || forcedNow;
					if (flag || !HasDigit(source))
					{
						mod.settings.AcceptLiveTranslation(source, translation);
						mod.settings.FlushLiveCache(flag);
					}
				}
			}
			catch
			{
			}
			forcedNow = false;
		}

		private static void Finish(string source, string outcome)
		{
			UrtLiveDiag.Outcome(outcome);
			State[source] = 2;
		}

		private static void Sample(string why, string provider, string source, string dst)
		{
			if (samples < 40 && !(why == "unchanged") && Interlocked.Increment(ref samples) <= 40)
			{
				MLog.Info("[URT] live2 " + why + " [" + provider + "]: \"" + Short(source) + "\" -> \"" + Short(dst) + "\"");
			}
		}

		private static void Bench(string provider, int ms)
		{
			BenchedUntil[provider] = Environment.TickCount + ms;
		}

		private static bool Benched(string provider)
		{
			int value;
			return BenchedUntil.TryGetValue(provider, out value) && Environment.TickCount - value < 0;
		}

		private static List<IUniversalTranslationProvider> FastProviders(TranslationProviderSettings s)
		{
			List<IUniversalTranslationProvider> list = new List<IUniversalTranslationProvider>();
			if (s.EnableMicrosoftEdge && !UrtOptEngine.IsCoolingDown("Microsoft Edge"))
			{
				list.Add(new MicrosoftEdgeTranslateProvider());
			}
			if (s.EnableAzure && !string.IsNullOrWhiteSpace(s.AzureApiKey) && !UrtOptEngine.IsCoolingDown("Azure Translator"))
			{
				list.Add(new AzureTranslateProvider());
			}
			if (s.EnableGoogleWeb)
			{
				list.Add(new GoogleWebTranslateProvider());
			}
			if (s.EnableYandex)
			{
				list.Add(new YandexTranslateProvider());
			}
			if (s.EnableLingva)
			{
				list.Add(new LingvaTranslateProvider());
			}
			if (s.EnableMyMemory)
			{
				list.Add(new MyMemoryTranslateProvider());
			}
			return list;
		}

		private static List<IUniversalTranslationProvider> SlowProviders(TranslationProviderSettings s)
		{
			List<IUniversalTranslationProvider> list = new List<IUniversalTranslationProvider>();
			if (s.EnableGemini && !string.IsNullOrWhiteSpace(s.GeminiApiKey))
			{
				list.Add(new GeminiTranslateProvider());
			}
			if (s.EnableCustomEndpoint && !string.IsNullOrWhiteSpace(s.CustomEndpointUrl))
			{
				list.Add(new CustomEndpointTranslateProvider());
			}
			return list;
		}

		private static TranslationProviderSettings ProviderSettings()
		{
			try
			{
				UniversalRimTranslatorMod mod = LoadedModManager.GetMod<UniversalRimTranslatorMod>();
				return (mod == null || mod.settings == null) ? null : mod.settings.BuildProviderSettingsForLive();
			}
			catch
			{
				return null;
			}
		}

		private static string Target()
		{
			string text = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			return string.IsNullOrWhiteSpace(text) ? "English" : text;
		}

		private static string Unmask(string text, List<string> tokens)
		{
			if (tokens == null || tokens.Count == 0)
			{
				return text;
			}
			for (int i = 0; i < tokens.Count; i++)
			{
				text = text.Replace("⟦" + i + "⟧", tokens[i]);
			}
			return text;
		}

		private static bool LooksLikePath(string text)
		{
			return text.IndexOf(":/", StringComparison.Ordinal) >= 0 || text.IndexOf(":\\", StringComparison.Ordinal) >= 0 || text.IndexOf("://", StringComparison.Ordinal) >= 0;
		}

		private static bool HasDigit(string text)
		{
			for (int i = 0; i < text.Length; i++)
			{
				if (char.IsDigit(text[i]))
				{
					return true;
				}
			}
			return false;
		}

		private static bool HasLetter(string text)
		{
			for (int i = 0; i < text.Length; i++)
			{
				if (char.IsLetter(text[i]))
				{
					return true;
				}
			}
			return false;
		}

		private static string Collapse(string text)
		{
			return Regex.Replace(text ?? "", "\\s+", " ").Trim();
		}

		private static string Short(string text)
		{
			text = (text ?? "").Replace("\n", "\\n").Replace("\r", "");
			return (text.Length > 110) ? (text.Substring(0, 110) + "…") : text;
		}
	}
	internal static class UrtLiveDiag
	{
		private sealed class Prov
		{
			public int Calls;

			public int Ok;

			public int Fail;

			public int Slow;

			public int Throttled;

			public long Ms;
		}

		private static readonly ConcurrentDictionary<string, Prov> Providers = new ConcurrentDictionary<string, Prov>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, int[]> Rejects = new ConcurrentDictionary<string, int[]>(StringComparer.Ordinal);

		private static readonly ConcurrentDictionary<string, int[]> Outcomes = new ConcurrentDictionary<string, int[]>(StringComparer.Ordinal);

		private static long workerMs;

		private static int workerCount;

		private static int workerMaxMs;

		private static int samples;

		public static void Http(string provider, bool ok, long ms, int status, int timeoutMs)
		{
			if (string.IsNullOrEmpty(provider))
			{
				provider = "?";
			}
			Prov orAdd = Providers.GetOrAdd(provider, (string _) => new Prov());
			Interlocked.Increment(ref orAdd.Calls);
			if (ok)
			{
				Interlocked.Increment(ref orAdd.Ok);
			}
			else
			{
				Interlocked.Increment(ref orAdd.Fail);
			}
			if (!ok && timeoutMs > 0 && ms >= timeoutMs - 500)
			{
				Interlocked.Increment(ref orAdd.Slow);
			}
			if (status == 429)
			{
				Interlocked.Increment(ref orAdd.Throttled);
			}
			Interlocked.Add(ref orAdd.Ms, ms);
		}

		public static bool Reject(string reason, string source, string translated)
		{
			if (samples < 30 && Interlocked.Increment(ref samples) <= 30)
			{
				MLog.Info("[URT] live rejected " + reason + ": \"" + Short(source) + "\" -> \"" + Short(translated) + "\"");
			}
			return Reject(reason);
		}

		private static string Short(string text)
		{
			text = (text ?? "").Replace("\n", "\\n").Replace("\r", "");
			return (text.Length > 110) ? (text.Substring(0, 110) + "…") : text;
		}

		public static bool Reject(string reason)
		{
			Interlocked.Increment(ref Rejects.GetOrAdd(reason, (string _) => new int[1])[0]);
			return false;
		}

		public static void Outcome(string what)
		{
			Interlocked.Increment(ref Outcomes.GetOrAdd(what, (string _) => new int[1])[0]);
		}

		public static void Worker(long ms)
		{
			Interlocked.Increment(ref workerCount);
			Interlocked.Add(ref workerMs, ms);
			int num = (int)Math.Min(2147483647L, ms);
			int num2;
			while ((num2 = workerMaxMs) < num && Interlocked.CompareExchange(ref workerMaxMs, num, num2) != num2)
			{
			}
		}

		public static string Line()
		{
			StringBuilder stringBuilder = new StringBuilder();
			int num = Interlocked.Exchange(ref workerCount, 0);
			long num2 = Interlocked.Exchange(ref workerMs, 0L);
			int value = Interlocked.Exchange(ref workerMaxMs, 0);
			ThreadPool.GetAvailableThreads(out var workerThreads, out var _);
			ThreadPool.GetMaxThreads(out var workerThreads2, out var _);
			stringBuilder.Append("texts=").Append(num).Append(" avg=")
				.Append((num == 0) ? 0 : (num2 / num))
				.Append("ms max=")
				.Append(value)
				.Append("ms")
				.Append(" pool busy=")
				.Append(workerThreads2 - workerThreads);
			stringBuilder.Append(" | outcome:");
			AppendCounts(stringBuilder, Outcomes);
			stringBuilder.Append(" | providers:");
			if (Providers.IsEmpty)
			{
				stringBuilder.Append(" none called");
			}
			foreach (KeyValuePair<string, Prov> item in Providers.OrderBy((KeyValuePair<string, Prov> k) => k.Key))
			{
				Prov value2 = item.Value;
				int num3 = Interlocked.Exchange(ref value2.Calls, 0);
				int value3 = Interlocked.Exchange(ref value2.Ok, 0);
				int num4 = Interlocked.Exchange(ref value2.Fail, 0);
				int num5 = Interlocked.Exchange(ref value2.Slow, 0);
				int num6 = Interlocked.Exchange(ref value2.Throttled, 0);
				long num7 = Interlocked.Exchange(ref value2.Ms, 0L);
				if (num3 != 0)
				{
					stringBuilder.Append(' ').Append(item.Key).Append(' ')
						.Append(value3)
						.Append('/')
						.Append(num3)
						.Append(" ok");
					if (num4 > 0)
					{
						stringBuilder.Append(" fail=").Append(num4);
					}
					if (num5 > 0)
					{
						stringBuilder.Append(" timeout=").Append(num5);
					}
					if (num6 > 0)
					{
						stringBuilder.Append(" 429=").Append(num6);
					}
					stringBuilder.Append(" avg=").Append(num7 / num3).Append("ms;");
				}
			}
			stringBuilder.Append(" | rejected:");
			AppendCounts(stringBuilder, Rejects);
			return stringBuilder.ToString();
		}

		private static void AppendCounts(StringBuilder sb, ConcurrentDictionary<string, int[]> map)
		{
			bool flag = false;
			foreach (KeyValuePair<string, int[]> item in map.OrderBy((KeyValuePair<string, int[]> k) => k.Key))
			{
				int num = Interlocked.Exchange(ref item.Value[0], 0);
				if (num != 0)
				{
					flag = true;
					sb.Append(' ').Append(item.Key).Append('=')
						.Append(num);
				}
			}
			if (!flag)
			{
				sb.Append(" -");
			}
		}
	}
	internal static class UrtLivePrewarmPriority
	{
		private struct RankedCandidate
		{
			public readonly TranslationCandidate Candidate;

			public readonly int Priority;

			public readonly int TextLength;

			public readonly string SourceLanguage;

			public readonly int OriginalIndex;

			public RankedCandidate(TranslationCandidate candidate, int priority, int textLength, string sourceLanguage, int originalIndex)
			{
				Candidate = candidate;
				Priority = priority;
				TextLength = textLength;
				SourceLanguage = sourceLanguage ?? "";
				OriginalIndex = originalIndex;
			}
		}

		private static readonly string[] UrgentMetadata = new string[14]
		{
			"letter", "quest", "incident", "notification", "message", "alert", "threat", "raid", "attack", "reportstring",
			"deathmessage", "gizmo", "commandlabel", "commanddesc"
		};

		private static readonly string[] VisibleMetadata = new string[22]
		{
			"label", "description", "tooltip", "helptext", "inspectstring", "title", "jobstring", "flavortext", "keyed", "letter",
			"quest", "incident", "notification", "message", "alert", "threat", "raid", "attack", "reportstring", "deathmessage",
			"gizmo", "command"
		};

		public static void Sort(List<TranslationCandidate> candidates)
		{
			SortCore(candidates, sourceLanguageFirst: false);
		}

		public static void SortBySourceLanguage(List<TranslationCandidate> candidates)
		{
			SortCore(candidates, sourceLanguageFirst: true);
		}

		public static int Compare(TranslationCandidate a, TranslationCandidate b)
		{
			return CompareRanked(Rank(a, 0), Rank(b, 0), sourceLanguageFirst: false);
		}

		private static void SortCore(List<TranslationCandidate> candidates, bool sourceLanguageFirst)
		{
			if (candidates != null && candidates.Count >= 2)
			{
				RankedCandidate[] array = new RankedCandidate[candidates.Count];
				for (int i = 0; i < candidates.Count; i++)
				{
					array[i] = Rank(candidates[i], i);
				}
				Array.Sort(array, (RankedCandidate a, RankedCandidate b) => CompareRanked(a, b, sourceLanguageFirst));
				for (int j = 0; j < array.Length; j++)
				{
					candidates[j] = array[j].Candidate;
				}
			}
		}

		private static int CompareRanked(RankedCandidate a, RankedCandidate b, bool sourceLanguageFirst)
		{
			if (sourceLanguageFirst)
			{
				int num = StringComparer.OrdinalIgnoreCase.Compare(a.SourceLanguage, b.SourceLanguage);
				if (num != 0)
				{
					return num;
				}
			}
			int priority = a.Priority;
			int num2 = priority.CompareTo(b.Priority);
			if (num2 != 0)
			{
				return num2;
			}
			priority = a.TextLength;
			int num3 = priority.CompareTo(b.TextLength);
			if (num3 != 0)
			{
				return num3;
			}
			int num4 = StringComparer.OrdinalIgnoreCase.Compare((a.Candidate == null) ? "" : a.Candidate.Key, (b.Candidate == null) ? "" : b.Candidate.Key);
			int result;
			if (num4 == 0)
			{
				priority = a.OriginalIndex;
				result = priority.CompareTo(b.OriginalIndex);
			}
			else
			{
				result = num4;
			}
			return result;
		}

		private static RankedCandidate Rank(TranslationCandidate candidate, int index)
		{
			if (candidate == null)
			{
				return new RankedCandidate(null, 3, 0, "", index);
			}
			string value = string.Join(" ", candidate.Type ?? "", candidate.DefType ?? "", candidate.Key ?? "", candidate.FilePath ?? "").ToLowerInvariant();
			int priority = ((!ContainsAny(value, UrgentMetadata)) ? ((string.Equals(candidate.Type, "Keyed", StringComparison.OrdinalIgnoreCase) || ContainsAny(value, VisibleMetadata)) ? 1 : 2) : 0);
			return new RankedCandidate(candidate, priority, (candidate.SourceText ?? "").Length, LanguageCodeMapper.Canonical(candidate.SourceLanguage), index);
		}

		private static bool ContainsAny(string value, string[] terms)
		{
			for (int i = 0; i < terms.Length; i++)
			{
				if (value.IndexOf(terms[i], StringComparison.Ordinal) >= 0)
				{
					return true;
				}
			}
			return false;
		}
	}
	internal static class UrtManual
	{
		private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private static bool loaded;

		public static int Count
		{
			get
			{
				EnsureLoaded();
				return Map.Count;
			}
		}

		private static void EnsureLoaded()
		{
			if (!loaded)
			{
				Load();
			}
		}

		private static string FilePath()
		{
			return Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "ManualOverrides.txt");
		}

		private static string Norm(string s)
		{
			return (s ?? "").Trim();
		}

		public static bool Has(string source)
		{
			EnsureLoaded();
			string text = Norm(source);
			return text.Length > 0 && Map.ContainsKey(text);
		}

		public static bool TryGet(string source, out string translation)
		{
			EnsureLoaded();
			translation = null;
			string text = Norm(source);
			if (text.Length == 0)
			{
				return false;
			}
			if (!Map.TryGetValue(text, out var value) || string.IsNullOrWhiteSpace(value))
			{
				return false;
			}
			translation = value;
			return true;
		}

		public static void CopySources(ICollection<string> into)
		{
			if (into == null)
			{
				return;
			}
			EnsureLoaded();
			foreach (KeyValuePair<string, string> item in Map)
			{
				if (!string.IsNullOrWhiteSpace(item.Key))
				{
					into.Add(item.Key);
				}
			}
		}

		public static void Put(string source, string translation)
		{
			string text = Norm(source);
			string text2 = Norm(translation);
			if (text.Length != 0 && text2.Length != 0)
			{
				loaded = true;
				Map[text] = text2;
				Save();
			}
		}

		public static void Load()
		{
			loaded = true;
			Map.Clear();
			try
			{
				string path = FilePath();
				if (!File.Exists(path))
				{
					return;
				}
				string[] array = File.ReadAllLines(path, Encoding.UTF8);
				foreach (string text in array)
				{
					if (string.IsNullOrWhiteSpace(text) || text.StartsWith("#"))
					{
						continue;
					}
					int num = text.IndexOf('\t');
					if (num > 0)
					{
						string text2 = text.Substring(0, num).Replace("\\t", "\t").Replace("\\n", "\n");
						string text3 = text.Substring(num + 1).Replace("\\t", "\t").Replace("\\n", "\n");
						if (text2.Length > 0 && text3.Length > 0)
						{
							Map[text2] = text3;
						}
					}
				}
			}
			catch
			{
			}
		}

		public static void Save()
		{
			try
			{
				string path = FilePath();
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				List<string> list = new List<string>();
				foreach (KeyValuePair<string, string> item in Map)
				{
					list.Add(item.Key.Replace("\t", "\\t").Replace("\n", "\\n") + "\t" + item.Value.Replace("\t", "\\t").Replace("\n", "\\n"));
				}
				File.WriteAllLines(path, list.ToArray(), Encoding.UTF8);
			}
			catch
			{
			}
		}

		public static void MergeInto(TranslationCache cache, string targetLang)
		{
			if (cache == null || Map.Count == 0)
			{
				return;
			}
			foreach (KeyValuePair<string, string> item in Map)
			{
				cache.Put(new CachedTranslation
				{
					ModName = "Manual",
					ModPackageId = "urt.manual",
					Type = "Keyed",
					DefType = "Manual",
					Key = "manual." + (uint)item.Key.GetHashCode(),
					SourceLanguage = "Auto",
					SourceText = item.Key,
					OriginalSourceText = item.Key,
					TargetLanguage = (string.IsNullOrWhiteSpace(targetLang) ? "English" : targetLang),
					Translation = item.Value,
					AutomaticTranslation = item.Value
				});
				RuntimeTextTranslator.RegisterLive(item.Key, item.Value);
			}
		}
	}
	internal static class UrtNet
	{
		private static bool done;

		public static void Configure()
		{
			if (done)
			{
				return;
			}
			done = true;
			try
			{
				if (ServicePointManager.DefaultConnectionLimit < 24)
				{
					ServicePointManager.DefaultConnectionLimit = 24;
				}
				ServicePointManager.Expect100Continue = false;
				ServicePointManager.UseNagleAlgorithm = false;
				MLog.Info("[URT] b97 network: 24 connections per server, no 100-continue wait, no Nagle delay.");
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] network settings not applied: " + ex.GetType().Name + " " + ex.Message);
			}
		}
	}
	internal static class UrtOptEngine
	{
		public sealed class ProviderCaps
		{
			public bool SupportsBatch;

			public int MaxBatchItems = 8;

			public int MaxBatchCharacters = 8000;

			public int MaxConcurrency = 2;

			public int TimeoutMs = 18000;
		}

		private sealed class ProviderHealth
		{
			public int Total;

			public int Ok;

			public int Fail;

			public int RateLimited;

			public int LatencySum;

			public int StreakFail;

			public DateTime CircuitUntil = DateTime.MinValue;
		}

		public sealed class ProviderCooldownException : Exception
		{
			public readonly int RemainingSeconds;

			public ProviderCooldownException(string provider, int remaining)
				: base((provider ?? "provider") + " cooldown active (" + remaining + "s)")
			{
				RemainingSeconds = remaining;
			}
		}

		private sealed class ProviderGate
		{
			private readonly SemaphoreSlim slim;

			public DateTime CoolUntil = DateTime.MinValue;

			public ProviderGate(int n)
			{
				slim = new SemaphoreSlim(Math.Max(1, n));
			}

			public Task WaitAsync()
			{
				return slim.WaitAsync();
			}

			public void Release()
			{
				try
				{
					slim.Release();
				}
				catch
				{
				}
			}
		}

		private const int RamTextLimit = 400000;

		private static readonly ConcurrentDictionary<string, string> RamText = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		private static readonly ConcurrentDictionary<string, Task<string>> InFlight = new ConcurrentDictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, ProviderHealth> Health = new ConcurrentDictionary<string, ProviderHealth>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, ProviderGate> Gates = new ConcurrentDictionary<string, ProviderGate>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, DateTime> Negative = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, string> FileStamps = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public static string CollapseWs(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}
			string text = value.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
			while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
			{
				text = text.Replace("  ", " ");
			}
			return text;
		}

		public static string TextKey(string source, string target)
		{
			string sourceLang = LanguageDetector.DetectEmbeddedSourceLanguage(source ?? "");
			return TextKey(source, sourceLang, target);
		}

		public static string TextKey(string source, string sourceLang, string target)
		{
			string text = LanguageCodeMapper.Canonical(sourceLang ?? "");
			if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase))
			{
				text = "English";
			}
			string text2 = LanguageCodeMapper.Canonical(target ?? "");
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = LanguageCodeMapper.Canonical(UniversalRimTranslatorSettings.PlayerTargetLanguage());
			}
			return CollapseWs(source ?? "") + "\n" + text + "\n" + text2;
		}

		public static bool TryRam(string source, string target, out string translation)
		{
			return RamText.TryGetValue(TextKey(source, target), out translation) && !string.IsNullOrWhiteSpace(translation);
		}

		public static bool TryRam(string source, string sourceLang, string target, out string translation)
		{
			return RamText.TryGetValue(TextKey(source, sourceLang, target), out translation) && !string.IsNullOrWhiteSpace(translation);
		}

		public static void PutRam(string source, string target, string translation)
		{
			PutRam(source, LanguageDetector.DetectEmbeddedSourceLanguage(source ?? ""), target, translation);
		}

		public static void PutRam(string source, string sourceLang, string target, string translation)
		{
			if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation) || !UrtResultValidator.Accept(source, translation))
			{
				return;
			}
			string text = translation.Trim();
			string key = TextKey(source, sourceLang, target);
			if (RamText.Count >= 400000 && !RamText.ContainsKey(key))
			{
				return;
			}
			RamText[key] = text;
			string text2 = LanguageDetector.DetectEmbeddedSourceLanguage(source ?? "");
			if (!string.IsNullOrWhiteSpace(text2))
			{
				string key2 = TextKey(source, text2, target);
				if (!RamText.TryGetValue(key2, out var value) || string.Equals(value, text, StringComparison.Ordinal))
				{
					RamText.TryAdd(key2, text);
				}
			}
		}

		public static async Task ForEachAsync(int count, int concurrency, Func<int, Task> body, CancellationToken ct)
		{
			if (count > 0 && body != null)
			{
				int j = Math.Max(1, Math.Min(concurrency, count));
				SemaphoreSlim sem = new SemaphoreSlim(j);
				Task[] tasks = new Task[count];
				for (int i = 0; i < count; i++)
				{
					int idx = i;
					tasks[i] = ForEachOneAsync(sem, idx, body, ct);
				}
				await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
			}
		}

		private static async Task ForEachOneAsync(SemaphoreSlim sem, int idx, Func<int, Task> body, CancellationToken ct)
		{
			await sem.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				await body(idx).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				sem.Release();
			}
		}

		public static bool LooksNonTranslatable(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return true;
			}
			string text2 = text.Trim();
			if (text2.Length < 2)
			{
				return true;
			}
			if (Regex.IsMatch(text2, "^[0-9.\\-/%$€]+$"))
			{
				return true;
			}
			if (text2.StartsWith("<") && text2.EndsWith(">") && text2.Length < 80)
			{
				return true;
			}
			if (Regex.IsMatch(text2, "^\\{[0-9A-Za-z_\\.:]+\\}$"))
			{
				return true;
			}
			if (text2.IndexOf("ThingDef", StringComparison.OrdinalIgnoreCase) >= 0 && text2.Length < 40)
			{
				return true;
			}
			return false;
		}

		public static ProviderCaps Caps(string name)
		{
			string text = name ?? "";
			if (text.IndexOf("Azure", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = true,
					MaxBatchItems = 100,
					MaxBatchCharacters = 49500,
					MaxConcurrency = 4,
					TimeoutMs = 60000
				};
			}
			if (text.IndexOf("Microsoft Edge", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = true,
					MaxBatchItems = 50,
					MaxBatchCharacters = 15000,
					MaxConcurrency = 2,
					TimeoutMs = 30000
				};
			}
			if (text.IndexOf("Custom endpoint", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = true,
					MaxBatchItems = 20,
					MaxBatchCharacters = 8000,
					MaxConcurrency = 1,
					TimeoutMs = 12000
				};
			}
			if (text.IndexOf("DeepL", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = true,
					MaxBatchItems = 50,
					MaxBatchCharacters = 20000,
					MaxConcurrency = 2,
					TimeoutMs = 20000
				};
			}
			if (text.IndexOf("Google Cloud", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = true,
					MaxBatchItems = 50,
					MaxBatchCharacters = 20000,
					MaxConcurrency = 2,
					TimeoutMs = 20000
				};
			}
			if (text.IndexOf("Gemini", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = true,
					MaxBatchItems = 20,
					MaxBatchCharacters = 12000,
					MaxConcurrency = 2,
					TimeoutMs = 45000
				};
			}
			if (text.IndexOf("Google web", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = false,
					MaxBatchItems = 1,
					MaxBatchCharacters = 4500,
					MaxConcurrency = 6,
					TimeoutMs = 6000
				};
			}
			if (text.IndexOf("Yandex", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = false,
					MaxBatchItems = 1,
					MaxBatchCharacters = 4500,
					MaxConcurrency = 4,
					TimeoutMs = 20000
				};
			}
			if (text.IndexOf("Simply", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = false,
					MaxBatchItems = 1,
					MaxBatchCharacters = 4500,
					MaxConcurrency = 8,
					TimeoutMs = 6000
				};
			}
			if (text.IndexOf("Lingva", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = false,
					MaxBatchItems = 1,
					MaxBatchCharacters = 4000,
					MaxConcurrency = 3,
					TimeoutMs = 15000
				};
			}
			if (text.IndexOf("MyMemory", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = false,
					MaxBatchItems = 1,
					MaxBatchCharacters = 500,
					MaxConcurrency = 2,
					TimeoutMs = 15000
				};
			}
			if (text.IndexOf("Libre", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return new ProviderCaps
				{
					SupportsBatch = false,
					MaxBatchItems = 1,
					MaxBatchCharacters = 4000,
					MaxConcurrency = 2,
					TimeoutMs = 12000
				};
			}
			return new ProviderCaps
			{
				SupportsBatch = false,
				MaxBatchItems = 8,
				MaxBatchCharacters = 8000,
				MaxConcurrency = 2,
				TimeoutMs = 18000
			};
		}

		public static async Task<T> GateAsync<T>(string provider, Func<Task<T>> work)
		{
			ProviderGate gate = GateFor(provider);
			if (gate.CoolUntil > DateTime.UtcNow)
			{
				throw new ProviderCooldownException(provider, CooldownRemainingSeconds(provider));
			}
			await gate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
			if (gate.CoolUntil > DateTime.UtcNow)
			{
				gate.Release();
				throw new ProviderCooldownException(provider, CooldownRemainingSeconds(provider));
			}
			Stopwatch sw = Stopwatch.StartNew();
			bool ok = false;
			int status = 0;
			try
			{
				T result = await work().ConfigureAwait(continueOnCapturedContext: false);
				if (result is TranslationProviderResult pr)
				{
					ok = pr.Success;
					status = pr.StatusCode;
				}
				else
				{
					ok = result != null;
				}
				return result;
			}
			finally
			{
				sw.Stop();
				Record(provider, ok, (int)sw.ElapsedMilliseconds, status);
				gate.Release();
			}
		}

		private static ProviderGate GateFor(string provider)
		{
			string key = provider ?? "?";
			return Gates.GetOrAdd(key, (string n) => new ProviderGate(Caps(n).MaxConcurrency));
		}

		public static string Fingerprint(string source, string target)
		{
			string text = TextKey(source, target);
			uint num = 2166136261u;
			for (int i = 0; i < text.Length; i++)
			{
				num = (num ^ text[i]) * 16777619;
			}
			return num.ToString("X8");
		}

		public static bool IsUsable(string provider)
		{
			return !IsCoolingDown(provider) && !CircuitOpen(provider);
		}

		public static int SecondsUntilUsable(string provider)
		{
			int num = CooldownRemainingSeconds(provider);
			if (Health.TryGetValue(provider ?? "?", out var value) && value != null && value.CircuitUntil > DateTime.UtcNow)
			{
				num = Math.Max(num, (int)Math.Ceiling((value.CircuitUntil - DateTime.UtcNow).TotalSeconds));
			}
			return Math.Max(0, num);
		}

		public static bool CircuitOpen(string provider)
		{
			if (!Health.TryGetValue(provider ?? "?", out var value) || value == null)
			{
				return false;
			}
			if (value.CircuitUntil == DateTime.MinValue || DateTime.UtcNow >= value.CircuitUntil)
			{
				return false;
			}
			return true;
		}

		public static bool NegativeHit(string fingerprint, string provider)
		{
			DateTime value;
			return Negative.TryGetValue(fingerprint + "|" + provider, out value) && value > DateTime.UtcNow;
		}

		public static void RememberNegative(string fingerprint, string provider, int seconds)
		{
			if (Negative.Count > 50000)
			{
				DateTime utcNow = DateTime.UtcNow;
				foreach (KeyValuePair<string, DateTime> item in Negative)
				{
					if (item.Value <= utcNow)
					{
						Negative.TryRemove(item.Key, out var _);
					}
				}
				if (Negative.Count > 50000)
				{
					Negative.Clear();
				}
			}
			Negative[fingerprint + "|" + provider] = DateTime.UtcNow.AddSeconds(Math.Max(15, seconds));
		}

		public static bool FileUnchanged(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
				{
					return false;
				}
				FileInfo fileInfo = new FileInfo(path);
				string text = fileInfo.Length + "|" + fileInfo.LastWriteTimeUtc.Ticks;
				if (FileStamps.TryGetValue(path, out var value) && value == text)
				{
					return true;
				}
				FileStamps[path] = text;
				return false;
			}
			catch
			{
				return false;
			}
		}

		public static void Record(string provider, bool success, int latencyMs, int statusCode)
		{
			ProviderHealth orAdd = Health.GetOrAdd(provider ?? "?", (string _) => new ProviderHealth());
			Interlocked.Increment(ref orAdd.Total);
			Interlocked.Add(ref orAdd.LatencySum, Math.Max(0, latencyMs));
			if (success)
			{
				Interlocked.Increment(ref orAdd.Ok);
				orAdd.StreakFail = 0;
				orAdd.CircuitUntil = DateTime.MinValue;
			}
			else
			{
				Interlocked.Increment(ref orAdd.Fail);
				int num = Interlocked.Increment(ref orAdd.StreakFail);
				if (num >= 5)
				{
					orAdd.CircuitUntil = DateTime.UtcNow.AddSeconds(60.0);
				}
			}
			switch (statusCode)
			{
			case 429:
			{
				Interlocked.Increment(ref orAdd.RateLimited);
				int seconds = (((provider ?? "").IndexOf("Google", StringComparison.OrdinalIgnoreCase) >= 0) ? 90 : 25);
				CoolDown(provider, seconds);
				break;
			}
			default:
				if (statusCode != 403)
				{
					break;
				}
				goto case 401;
			case 401:
				CoolDown(provider, 600);
				orAdd.CircuitUntil = DateTime.UtcNow.AddSeconds(600.0);
				break;
			}
		}

		public static void CoolDown(string provider, int seconds)
		{
			ProviderGate providerGate = GateFor(provider);
			DateTime dateTime = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
			lock (providerGate)
			{
				if (dateTime > providerGate.CoolUntil)
				{
					providerGate.CoolUntil = dateTime;
				}
			}
		}

		public static void ClearCooldown(string provider)
		{
			if (Gates.TryGetValue(provider ?? "?", out var value) && value != null)
			{
				lock (value)
				{
					value.CoolUntil = DateTime.MinValue;
				}
			}
			if (Health.TryGetValue(provider ?? "?", out var value2) && value2 != null)
			{
				value2.CircuitUntil = DateTime.MinValue;
				value2.StreakFail = 0;
			}
		}

		public static bool IsCoolingDown(string provider)
		{
			if (!Gates.TryGetValue(provider ?? "?", out var value) || value == null)
			{
				return false;
			}
			return value.CoolUntil > DateTime.UtcNow;
		}

		public static int CooldownRemainingSeconds(string provider)
		{
			if (!Gates.TryGetValue(provider ?? "?", out var value) || value == null)
			{
				return 0;
			}
			double totalSeconds = (value.CoolUntil - DateTime.UtcNow).TotalSeconds;
			return (!(totalSeconds <= 0.0)) ? ((int)Math.Ceiling(totalSeconds)) : 0;
		}

		public static bool ShouldRetry(int statusCode, int attempt)
		{
			if (attempt >= 2)
			{
				return false;
			}
			if (statusCode == 429 || statusCode == 408)
			{
				return true;
			}
			if (statusCode >= 500 && statusCode <= 599)
			{
				return true;
			}
			if (statusCode == 0)
			{
				return attempt == 0;
			}
			return false;
		}

		public static int BackoffMs(int attempt)
		{
			if (attempt <= 0)
			{
				return 500;
			}
			return attempt switch
			{
				1 => 1000, 
				2 => 2000, 
				_ => 4000, 
			};
		}

		public static string HealthLine()
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, ProviderHealth> item in Health)
			{
				ProviderHealth value = item.Value;
				int num = Math.Max(1, value.Total);
				list.Add(item.Key + " " + value.Ok * 100 / num + "% " + value.LatencySum / num + "ms");
			}
			return (list.Count == 0) ? "" : string.Join(" | ", list);
		}
	}
	public static class UrtPackLibrary
	{
		private sealed class Sink
		{
			public Dictionary<string, string> ByEnglish;

			public Dictionary<string, string> ByKey;

			public Dictionary<string, string> SourceByKey;
		}

		private static readonly object Gate = new object();

		private static Dictionary<string, string> byEnglish;

		private static Dictionary<string, string> byKey;

		private static Dictionary<string, string> sourceByKey;

		private static string loadedFor;

		private static int loadedFiles;

		private static volatile bool loading;

		public static string PacksFolder => Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "Packs");

		public static string Status
		{
			get
			{
				lock (Gate)
				{
					return ((byEnglish != null) ? byEnglish.Count : 0) + " reusable translations from " + loadedFiles + " files.";
				}
			}
		}

		public static void Reload(string target)
		{
			lock (Gate)
			{
				if (loading)
				{
					return;
				}
				byEnglish = null;
				byKey = null;
				sourceByKey = null;
				loadedFor = null;
				loadedFiles = 0;
			}
			Ensure(target);
		}

		public static bool Try(string source, string type, string defType, string key, string packageId, string target, out string translation)
		{
			translation = null;
			string text = LanguageCodeMapper.Canonical(target ?? "");
			if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "English", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			Ensure(target);
			lock (Gate)
			{
				if (byEnglish == null || byKey == null || !string.Equals(loadedFor, text, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				string text2 = TypeKey(packageId, type, defType, key);
				if (!string.IsNullOrEmpty(text2) && byKey.TryGetValue(text2, out var value) && SourceMatchesKey(text2, source) && Good(source, value, target))
				{
					translation = value;
					return true;
				}
				string text3 = TypeKey("", type, defType, key);
				if (!string.IsNullOrEmpty(text3) && byKey.TryGetValue(text3, out value) && SourceMatchesKey(text3, source) && Good(source, value, target))
				{
					translation = value;
					return true;
				}
				string text4 = Norm(source);
				if (text4.Length >= 2 && byEnglish.TryGetValue(text4, out value) && Good(source, value, target))
				{
					translation = value;
					return true;
				}
			}
			return false;
		}

		private static bool SourceMatchesKey(string id, string source)
		{
			string value;
			return sourceByKey != null && sourceByKey.TryGetValue(id, out value) && string.Equals(Norm(source), Norm(value), StringComparison.OrdinalIgnoreCase);
		}

		public static void Ensure(string target)
		{
			string text = LanguageCodeMapper.Canonical(target ?? "");
			if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "English", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			lock (Gate)
			{
				if ((byEnglish != null && string.Equals(loadedFor, text, StringComparison.OrdinalIgnoreCase)) || loading)
				{
					return;
				}
				loading = true;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> dictionary2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> dictionary3 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Sink sink = new Sink
			{
				ByEnglish = dictionary,
				ByKey = dictionary2,
				SourceByKey = dictionary3
			};
			int num = 0;
			try
			{
				foreach (ModContentPack item in LoadedModManager.RunningModsListForReading)
				{
					if (item != null && !string.IsNullOrEmpty(item.RootDir))
					{
						List<string> list = ModScanner.LoadFolders(item);
						for (int i = 0; i < list.Count; i++)
						{
							num += ReadMod(list[i], item.PackageId ?? "", text, sink);
						}
					}
				}
				string packsFolder = PacksFolder;
				if (Directory.Exists(packsFolder))
				{
					num += ReadMod(packsFolder, "", text, sink);
					string[] directories = Directory.GetDirectories(packsFolder);
					foreach (string root in directories)
					{
						num += ReadMod(root, "", text, sink);
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[URT] Pack library scan failed: " + ex.Message);
			}
			lock (Gate)
			{
				byEnglish = dictionary;
				byKey = dictionary2;
				sourceByKey = dictionary3;
				loadedFor = text;
				loadedFiles = num;
				loading = false;
			}
			MLog.Info("[URT] Pack library: " + dictionary.Count + " ready translations for " + text + " from " + num + " files.");
		}

		private static List<string> LanguageDirs(string root, string lang)
		{
			List<string> list = new List<string>();
			string path = Path.Combine(root, "Languages");
			if (!Directory.Exists(path))
			{
				return list;
			}
			try
			{
				string[] directories = Directory.GetDirectories(path);
				foreach (string text in directories)
				{
					string fileName = Path.GetFileName(text);
					if (string.Equals(LanguageCodeMapper.Canonical(LanguageNormalizer.Normalize(fileName)), lang, StringComparison.OrdinalIgnoreCase))
					{
						list.Add(text);
					}
				}
			}
			catch
			{
			}
			return list;
		}

		private static int ReadMod(string root, string packageId, string lang, Sink sink)
		{
			int num = 0;
			List<string> list = LanguageDirs(root, "English");
			List<string> list2 = LanguageDirs(root, lang);
			if (list2.Count == 0)
			{
				return 0;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < list.Count; i++)
			{
				LoadDir(Path.Combine(list[i], "Keyed"), dictionary);
			}
			Dictionary<string, Dictionary<string, string>> dictionary2 = null;
			for (int j = 0; j < list2.Count; j++)
			{
				num += Pair(dictionary, Path.Combine(list2[j], "Keyed"), packageId, "Keyed", "", lang, sink);
				string path = Path.Combine(list2[j], "DefInjected");
				if (!Directory.Exists(path))
				{
					continue;
				}
				string[] directories = Directory.GetDirectories(path);
				foreach (string text in directories)
				{
					string fileName = Path.GetFileName(text);
					Dictionary<string, string> dictionary3 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
					for (int l = 0; l < list.Count; l++)
					{
						LoadDir(Path.Combine(list[l], "DefInjected", fileName), dictionary3);
					}
					if (dictionary2 == null)
					{
						dictionary2 = ReadDefSources(root);
					}
					if (dictionary2.TryGetValue(fileName, out var value))
					{
						foreach (KeyValuePair<string, string> item in value)
						{
							if (!dictionary3.ContainsKey(item.Key))
							{
								dictionary3[item.Key] = item.Value;
							}
						}
					}
					num += Pair(dictionary3, text, packageId, "DefInjected", fileName, lang, sink);
				}
			}
			return num;
		}

		private static Dictionary<string, Dictionary<string, string>> ReadDefSources(string root)
		{
			Dictionary<string, Dictionary<string, string>> dictionary = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
			string path = Path.Combine(root, "Defs");
			if (!Directory.Exists(path))
			{
				return dictionary;
			}
			string[] files;
			try
			{
				files = Directory.GetFiles(path, "*.xml", SearchOption.AllDirectories);
			}
			catch
			{
				return dictionary;
			}
			for (int i = 0; i < files.Length; i++)
			{
				try
				{
					XmlDocument xmlDocument = new XmlDocument();
					xmlDocument.Load(files[i]);
					if (xmlDocument.DocumentElement == null)
					{
						continue;
					}
					foreach (XmlNode childNode in xmlDocument.DocumentElement.ChildNodes)
					{
						if (childNode.NodeType != XmlNodeType.Element)
						{
							continue;
						}
						XmlNode xmlNode2 = childNode["defName"];
						if (xmlNode2 == null || string.IsNullOrWhiteSpace(xmlNode2.InnerText))
						{
							continue;
						}
						string text = xmlNode2.InnerText.Trim();
						if (!dictionary.TryGetValue(childNode.Name, out var value))
						{
							value = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
							dictionary[childNode.Name] = value;
						}
						foreach (XmlNode childNode2 in childNode.ChildNodes)
						{
							if (childNode2.NodeType == XmlNodeType.Element && !(childNode2.Name == "defName") && (!childNode2.HasChildNodes || childNode2.FirstChild.NodeType == XmlNodeType.Text || childNode2.FirstChild.NodeType == XmlNodeType.CDATA))
							{
								string innerText = childNode2.InnerText;
								if (!string.IsNullOrWhiteSpace(innerText))
								{
									value[text + "." + childNode2.Name] = innerText.Trim();
								}
							}
						}
					}
				}
				catch
				{
				}
			}
			return dictionary;
		}

		private static int Pair(Dictionary<string, string> english, string targetDir, string packageId, string type, string defType, string lang, Sink sink)
		{
			if (!Directory.Exists(targetDir))
			{
				return 0;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int result = LoadDir(targetDir, dictionary);
			foreach (KeyValuePair<string, string> item in dictionary)
			{
				string value = item.Value;
				if (!string.IsNullOrWhiteSpace(value) && english.TryGetValue(item.Key, out var value2) && Good(value2, value, lang))
				{
					string text = TypeKey(packageId, type, defType, item.Key);
					if (!string.IsNullOrEmpty(text) && !sink.ByKey.ContainsKey(text))
					{
						sink.ByKey[text] = value.Trim();
						sink.SourceByKey[text] = value2;
					}
					string text2 = TypeKey("", type, defType, item.Key);
					if (!string.IsNullOrEmpty(text2) && !sink.ByKey.ContainsKey(text2))
					{
						sink.ByKey[text2] = value.Trim();
						sink.SourceByKey[text2] = value2;
					}
					sink.ByEnglish[Norm(value2)] = value.Trim();
				}
			}
			return result;
		}

		private static int LoadDir(string dir, Dictionary<string, string> into)
		{
			int num = 0;
			string[] files;
			try
			{
				files = Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories);
			}
			catch
			{
				return 0;
			}
			for (int i = 0; i < files.Length; i++)
			{
				string fileName = Path.GetFileName(files[i]);
				if (fileName.StartsWith("URT_", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				try
				{
					XmlDocument xmlDocument = new XmlDocument();
					xmlDocument.Load(files[i]);
					if (xmlDocument.DocumentElement != null)
					{
						ReadNodes(xmlDocument.DocumentElement, into);
						num++;
					}
				}
				catch
				{
				}
			}
			return num;
		}

		private static void ReadNodes(XmlNode node, Dictionary<string, string> into)
		{
			foreach (XmlNode childNode in node.ChildNodes)
			{
				if (childNode.NodeType != XmlNodeType.Element)
				{
					continue;
				}
				if (childNode.HasChildNodes && childNode.FirstChild.NodeType != XmlNodeType.Text && childNode.FirstChild.NodeType != XmlNodeType.CDATA)
				{
					ReadNodes(childNode, into);
					continue;
				}
				string innerText = childNode.InnerText;
				if (!string.IsNullOrWhiteSpace(innerText) && !into.ContainsKey(childNode.Name))
				{
					into[childNode.Name] = innerText.Trim();
				}
			}
		}

		private static string TypeKey(string packageId, string type, string defType, string key)
		{
			if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(type))
			{
				return "";
			}
			return packageId + "|" + type + "|" + defType + "|" + key;
		}

		private static bool Good(string source, string translated, string target)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(source) && string.Equals(Norm(source), Norm(translated), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (LanguageDetector.IsHalfTranslated(translated, target))
			{
				return false;
			}
			return true;
		}

		private static string Norm(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			StringBuilder stringBuilder = new StringBuilder(text.Length);
			bool flag = false;
			foreach (char c in text)
			{
				if (char.IsWhiteSpace(c))
				{
					flag = stringBuilder.Length > 0;
					continue;
				}
				if (flag)
				{
					stringBuilder.Append(' ');
					flag = false;
				}
				stringBuilder.Append(char.ToLowerInvariant(c));
			}
			return stringBuilder.ToString();
		}
	}
	// b114: texture / graphic / file paths, defNames, class names and other technical strings are never translated
	internal static class UrtNonText
	{
		private static readonly string[] FileExtensions = new string[22]
		{
			".png", ".jpg", ".jpeg", ".dds", ".psd", ".tga", ".bmp", ".gif", ".wav", ".ogg",
			".mp3", ".xml", ".dll", ".txt", ".json", ".shader", ".mat", ".prefab", ".asset", ".bundle",
			".csv", ".exe"
		};

		private static readonly HashSet<string> PathRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Things", "UI", "Sounds", "Textures", "Icons", "World", "Map", "Weather", "Terrain", "Building",
			"Buildings", "Item", "Items", "Pawn", "Pawns", "Apparel", "Weapons", "Plant", "Plants", "Mote",
			"Motes", "Effects", "Other", "Interaction", "Materials", "Shaders", "Sound", "Music", "Ambient", "Misc",
			"Animal", "Animals", "Ammo", "Projectile", "Projectiles", "Filth", "Gene", "Genes", "Xenotype", "Ideo"
		};

		private static readonly Regex RichTag = new Regex("<[^<>\\r\\n]*>", RegexOptions.Compiled);

		private static readonly Regex Escapes = new Regex("\\\\[nrt]", RegexOptions.Compiled);

		private static readonly Regex SnakeIdentifier = new Regex("^[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+$", RegexOptions.Compiled);

		public static bool IsNonTextField(string field)
		{
			string text = (field ?? "").Trim();
			int num = text.LastIndexOf('.');
			if (num >= 0)
			{
				text = text.Substring(num + 1);
			}
			text = text.ToLowerInvariant();
			if (text.Length == 0)
			{
				return false;
			}
			switch (text)
			{
			case "defname":
			case "packageid":
			case "modid":
			case "texpath":
			case "graphicpath":
			case "iconpath":
			case "uiiconpath":
			case "texname":
			case "shadertype":
			case "shader":
			case "url":
			case "filename":
			case "folder":
			case "tag":
			case "tags":
			case "texture":
				return true;
			}
			return text.EndsWith("path", StringComparison.Ordinal) || text.EndsWith("defname", StringComparison.Ordinal) || text.EndsWith("class", StringComparison.Ordinal) || text.EndsWith("texture", StringComparison.Ordinal) || text.EndsWith("url", StringComparison.Ordinal) || text.EndsWith("texname", StringComparison.Ordinal);
		}

		public static bool IsNonTextKey(string key)
		{
			if (string.IsNullOrWhiteSpace(key) || key.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase) || key.IndexOf('.') < 0)
			{
				return false;
			}
			return IsNonTextField(key);
		}

		public static bool LooksLikeNonText(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return false;
			}
			string text = value.Trim();
			if (text.IndexOfAny(Suspicious) < 0)
			{
				return false;
			}
			if (text.IndexOf("://", StringComparison.Ordinal) >= 0 && !HasWhitespace(text))
			{
				return true;
			}
			string text2 = text;
			if (text2.IndexOf('<') >= 0)
			{
				text2 = RichTag.Replace(text2, "");
			}
			if (text2.IndexOf('\\') >= 0)
			{
				text2 = Escapes.Replace(text2, " ");
			}
			text2 = text2.Trim();
			if (text2.Length == 0 || HasWhitespace(text2))
			{
				return false;
			}
			for (int i = 0; i < FileExtensions.Length; i++)
			{
				if (text2.EndsWith(FileExtensions[i], StringComparison.OrdinalIgnoreCase) && text2.Length > FileExtensions[i].Length)
				{
					return true;
				}
			}
			if (text2.IndexOf('\\') >= 0)
			{
				return true;
			}
			int num = text2.IndexOf('/');
			if (num >= 0)
			{
				int num2 = 0;
				for (int j = 0; j < text2.Length; j++)
				{
					if (text2[j] == '/')
					{
						num2++;
					}
				}
				if (num2 >= 2 || num == 0 || num == text2.Length - 1 || PathRoots.Contains(text2.Substring(0, num)))
				{
					return true;
				}
			}
			return text2.IndexOf('_') > 0 && SnakeIdentifier.IsMatch(text2);
		}

		private static readonly char[] Suspicious = new char[5] { '/', '\\', '_', '.', ':' };

		private static bool HasWhitespace(string text)
		{
			for (int i = 0; i < text.Length; i++)
			{
				if (char.IsWhiteSpace(text[i]))
				{
					return true;
				}
			}
			return false;
		}
	}
	internal static class UrtResultValidator
	{
		private static readonly Regex ErrorLike = new Regex("\\b(error|exception|rate.?limit|too many requests|quota|unauthorized|forbidden|invalid api)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static readonly Regex ProtectedToken = new Regex("\\{\\{[0-9]+\\}\\}|\\{[^{}\\r\\n]+\\}|\\$[A-Za-z_][A-Za-z0-9_\\.]*|\\[\\[[^\\]]+\\]\\]|\\[[A-Za-z_][A-Za-z0-9_\\.]*\\]|%(?:\\d+\\$)?[-+#0]*(?:\\d+|\\*)?(?:\\.(?:\\d+|\\*))?[hlLzjtq]*[diuoxXfFeEgGaAcspn%]|</?[A-Za-z][^<>]*>|\\\\[nrt]|\\(\\*[A-Za-z_][^)\\r\\n]*\\)|\\(/[A-Za-z_][A-Za-z0-9_]*\\)", RegexOptions.Compiled);

		private static readonly Regex LeakedMask = new Regex("\\[\\[#|⟦|⟧|URT_PLACEHOLDER|URTTOKEN|RIMTOKEN[0-9]|\\bURT_?[0-9]", RegexOptions.Compiled);

		private static readonly Regex JudgeToken = new Regex("(\\{[^{}\\r\\n]*\\}|\\[[^\\]\\r\\n]*\\]|<[^>\\r\\n]+>|^\\s*[A-Za-z_][A-Za-z0-9_]*(?:\\([^)\\r\\n]*\\))?\\s*->)", RegexOptions.Compiled);

		private static readonly Regex JudgeWord = new Regex("\\p{L}{4,}", RegexOptions.Compiled);

		private static readonly Regex RulePrefix = new Regex("^\\s*[A-Za-z_][A-Za-z0-9_]*(?:\\([^)\\r\\n]*\\))?\\s*->", RegexOptions.Compiled);

		public static bool Accept(string source, string translation)
		{
			if (string.IsNullOrWhiteSpace(translation))
			{
				return false;
			}
			string text = (source ?? "").Trim();
			string text2 = translation.Trim();
			if (text2.Length == 0)
			{
				return false;
			}
			if (text2.IndexOf('<') >= 0 && text2.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
			if (ErrorLike.IsMatch(text2) && !ErrorLike.IsMatch(text))
			{
				return false;
			}
			if (text.Length >= 12 && text2.Length * 4 < text.Length && !HasDenseScript(text2))
			{
				return false;
			}
			if (LooksLikeLeakedMask(text2))
			{
				return false;
			}
			if (!ProtectedTokensMatch(text, text2))
			{
				return false;
			}
			if (LooksSpliced(text, text2))
			{
				return false;
			}
			if (!RulePrefixKept(text, text2))
			{
				return false;
			}
			return true;
		}

		public static string Judge(string source, string translated, string target)
		{
			if (string.IsNullOrWhiteSpace(translated))
			{
				return "empty";
			}
			if (translated.IndexOf("[[#", StringComparison.Ordinal) >= 0)
			{
				return "maskLeft";
			}
			if (!Accept(source, translated))
			{
				return LooksSpliced(source, translated) ? "spliced" : ((!RulePrefixKept(source, translated)) ? "ruleNameLost" : "tokensOrError");
			}
			if (LanguageDetector.HasResidualForeignScript(translated, target))
			{
				return "foreignLeft";
			}
			string text = Regex.Replace(source ?? "", "\\s+", " ").Trim();
			string text2 = Regex.Replace(translated, "\\s+", " ").Trim();
			if (text.Length > 0 && string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
			{
				return (LanguageDetector.LooksLikeProperNameOrCode(source) && text.Length <= 24) ? null : "unchanged";
			}
			if (MostWordsKept(source, translated))
			{
				return "halfTranslated";
			}
			if (CountWords(text2) >= 6)
			{
				string text3 = JudgeToken.Replace(text2, " ").Replace("\\n", " ");
				if (!LanguageDetector.LooksLikeTargetText(text3, target) && !string.Equals(LanguageCodeMapper.Canonical(target), "English", StringComparison.OrdinalIgnoreCase) && LanguageDetector.HasEnglishSentence(text3))
				{
					return "wrongLanguage";
				}
			}
			return null;
		}

		private static bool MostWordsKept(string source, string translated)
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (Match item in JudgeWord.Matches(JudgeToken.Replace(source ?? "", " ")))
			{
				hashSet.Add(item.Value);
			}
			if (hashSet.Count < 3)
			{
				return false;
			}
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (Match item2 in JudgeWord.Matches(JudgeToken.Replace(translated ?? "", " ")))
			{
				if (hashSet.Contains(item2.Value))
				{
					hashSet2.Add(item2.Value);
				}
			}
			return hashSet2.Count * 2 >= hashSet.Count;
		}

		private static int CountWords(string text)
		{
			int num = 0;
			bool flag = false;
			string text2 = text ?? "";
			foreach (char c in text2)
			{
				bool flag2 = char.IsLetter(c);
				if (flag2 && !flag)
				{
					num++;
				}
				flag = flag2;
			}
			return num;
		}

		public static string RulePrefixOf(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			Match match = RulePrefix.Match(text);
			return match.Success ? match.Value : null;
		}

		public static bool RulePrefixKept(string source, string translation)
		{
			string text = RulePrefixOf(source);
			if (text == null)
			{
				return true;
			}
			string text2 = RulePrefixOf(translation);
			return text2 != null && string.Equals(text.Replace(" ", ""), text2.Replace(" ", ""), StringComparison.Ordinal);
		}

		public static bool LooksSpliced(string source, string translation)
		{
			if (string.IsNullOrEmpty(translation))
			{
				return false;
			}
			string text = source ?? "";
			string text2 = translation.Trim();
			if (text2.IndexOf('\n') >= 0 || text2.IndexOf("\\n", StringComparison.Ordinal) >= 0)
			{
				return false;
			}
			if (text2.Length <= 200 && text2.IndexOf("  ", StringComparison.Ordinal) >= 0 && text.IndexOf("  ", StringComparison.Ordinal) < 0)
			{
				return true;
			}
			if (text2.Length > 60)
			{
				return false;
			}
			return GluedWord(text, text2) != null;
		}

		private static string GluedWord(string source, string translation)
		{
			List<string> list = new List<string>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string item in Words(source))
			{
				hashSet.Add(item);
				if (item.Length >= 3 && IsAscii(item))
				{
					list.Add(item);
				}
			}
			if (list.Count == 0)
			{
				return null;
			}
			string text = Squash(source);
			int i = 0;
			while (i < translation.Length)
			{
				if (!char.IsLetter(translation[i]))
				{
					i++;
					continue;
				}
				int num = i;
				for (; i < translation.Length && char.IsLetter(translation[i]); i++)
				{
				}
				if (num > 0 && translation[num - 1] == '\\')
				{
					continue;
				}
				string text2 = translation.Substring(num, i - num);
				if (hashSet.Contains(text2) || text.IndexOf(text2, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					continue;
				}
				for (int j = 0; j < list.Count; j++)
				{
					string text3 = list[j];
					if (text2.Length > text3.Length && text2.EndsWith(text3, StringComparison.OrdinalIgnoreCase))
					{
						int num2 = text2.Length - text3.Length;
						char c = text2[num2 - 1];
						bool flag = char.IsLower(c) && char.IsUpper(text2[num2]);
						if (text3.Length >= 4 && num2 >= 2)
						{
							return text2;
						}
						if (text3.Length == 3 && (flag || (num2 >= 3 && c > '\u007f')))
						{
							return text2;
						}
					}
				}
			}
			return null;
		}

		private static IEnumerable<string> Words(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				yield break;
			}
			int i = 0;
			while (i < text.Length)
			{
				if (!char.IsLetter(text[i]))
				{
					i++;
					continue;
				}
				int start = i;
				while (i < text.Length && char.IsLetter(text[i]))
				{
					i++;
				}
				yield return text.Substring(start, i - start);
			}
		}

		private static bool IsAscii(string w)
		{
			for (int i = 0; i < w.Length; i++)
			{
				if (w[i] > '\u007f')
				{
					return false;
				}
			}
			return true;
		}

		private static string Squash(string text)
		{
			StringBuilder stringBuilder = new StringBuilder(text.Length);
			foreach (char c in text)
			{
				if (!char.IsWhiteSpace(c) && c != '-' && c != '_')
				{
					stringBuilder.Append(c);
				}
			}
			return stringBuilder.ToString();
		}

		public static bool LooksLikeLeakedMask(string text)
		{
			return !string.IsNullOrEmpty(text) && LeakedMask.IsMatch(text);
		}

		public static bool HasDenseScript(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			foreach (int num in text)
			{
				if ((num >= 12352 && num <= 12543) || (num >= 13312 && num <= 40959) || (num >= 63744 && num <= 64255) || (num >= 44032 && num <= 55215))
				{
					return true;
				}
			}
			return false;
		}

		private static bool ProtectedTokensMatch(string source, string translation)
		{
			Dictionary<string, int> dictionary = CountTokens(source);
			if (dictionary.Count == 0)
			{
				return true;
			}
			Dictionary<string, int> dictionary2 = CountTokens(translation);
			foreach (KeyValuePair<string, int> item in dictionary)
			{
				if (!dictionary2.TryGetValue(item.Key, out var value) || value < item.Value)
				{
					return false;
				}
			}
			return true;
		}

		private static Dictionary<string, int> CountTokens(string text)
		{
			Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
			if (string.IsNullOrEmpty(text))
			{
				return dictionary;
			}
			foreach (Match item in ProtectedToken.Matches(text))
			{
				dictionary.TryGetValue(item.Value, out var value);
				dictionary[item.Value] = value + 1;
			}
			return dictionary;
		}
	}
	internal static class UrtScriptSplit
	{
		public static bool IsMixed(string text)
		{
			if (string.IsNullOrEmpty(text) || text.Length < 4)
			{
				return false;
			}
			HashSet<int> hashSet = new HashSet<int>();
			foreach (char c in text)
			{
				int num = ClassOf(c);
				if (num >= 1)
				{
					hashSet.Add(num);
				}
				if (hashSet.Count >= 2)
				{
					return true;
				}
			}
			return false;
		}

		public static List<string> Runs(string text)
		{
			List<string> list = new List<string>();
			if (string.IsNullOrEmpty(text))
			{
				return list;
			}
			StringBuilder stringBuilder = new StringBuilder();
			int num = -2;
			foreach (char c in text)
			{
				int num2 = ClassOf(c);
				int num3 = ((num2 == 0) ? num : num2);
				if (num3 != num && stringBuilder.Length > 0)
				{
					list.Add(stringBuilder.ToString());
					stringBuilder.Length = 0;
				}
				if (num2 != 0)
				{
					num = num2;
				}
				stringBuilder.Append(c);
			}
			if (stringBuilder.Length > 0)
			{
				list.Add(stringBuilder.ToString());
			}
			return list;
		}

		public static int ClassOf(char c)
		{
			if ((c >= '\u3040' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ'))
			{
				return 2;
			}
			if ((c >= '㐀' && c <= '\u9fff') || (c >= '豈' && c <= '\ufaff'))
			{
				return 3;
			}
			if ((c >= '가' && c <= '\ud7af') || (c >= 'ᄀ' && c <= 'ᇿ'))
			{
				return 4;
			}
			if (c >= 'Ѐ' && c <= 'ԯ')
			{
				return 5;
			}
			if (c >= '\u0600' && c <= 'ۿ')
			{
				return 6;
			}
			if (c >= 'Ͱ' && c <= 'Ͽ')
			{
				return 7;
			}
			if (c >= '\u0590' && c <= '\u05ff')
			{
				return 8;
			}
			if (c >= '\u0e00' && c <= '\u0e7f')
			{
				return 9;
			}
			if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '\'')
			{
				return 1;
			}
			if ((c >= 'À' && c <= 'ɏ' && c != '×' && c != '÷') || (c >= 'Ḁ' && c <= 'ỿ'))
			{
				return 1;
			}
			return 0;
		}

		public static bool IsForeignRun(string part)
		{
			if (string.IsNullOrWhiteSpace(part) || part.Trim().Length < 2)
			{
				return false;
			}
			foreach (char c in part)
			{
				int num = ClassOf(c);
				if (num >= 2)
				{
					return true;
				}
			}
			return false;
		}
	}
	internal static class UrtVersion
	{
		public const string Number = "0.9.57";

		public const string Build = "b115";

		public const string Display = "0.9.55-b115";

		public const string LogTag = "[Universal RimTranslator]";
	}
	public sealed class YandexTranslateProvider : IUniversalTranslationProvider
	{
		private readonly HttpClient client;

		public string Name => "Yandex";

		public YandexTranslateProvider()
		{
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_0013: Expected O, but got Unknown
			client = new HttpClient();
			client.Timeout = TimeSpan.FromSeconds(20.0);
		}

		public async Task<TranslationProviderResult> TranslateBatchAsync(IList<string> texts, string source, string target, TranslationProviderSettings settings, CancellationToken cancellationToken)
		{
			TranslationProviderResult result = new TranslationProviderResult();
			if (texts == null || texts.Count == 0)
			{
				result.Error = "Batch is empty.";
				return result;
			}
			string targetCode = Code(target);
			string sourceCode = Code(source);
			string lang = ((string.IsNullOrEmpty(sourceCode) || sourceCode == "auto") ? ("auto-" + targetCode) : (sourceCode + "-" + targetCode));
			string[] endpoints = new string[2] { "https://browser.translate.yandex.net/api/v1/tr.json/translate", "https://translate.yandex.net/api/v1/tr.json/translate" };
			int failedItems = 0;
			string[] slots = new string[texts.Count];
			int[] statusCodes = new int[texts.Count];
			string[] errorSlots = new string[texts.Count];
			await UrtOptEngine.ForEachAsync(texts.Count, 2, async delegate(int i)
			{
				string text = texts[i] ?? "";
				if (text.Length > 3500)
				{
					slots[i] = "";
					errorSlots[i] = "text exceeds 3500 characters";
					Interlocked.Increment(ref failedItems);
				}
				else
				{
					string lastHttp = "";
					string[] array = endpoints;
					foreach (string endpoint in array)
					{
						StringBuilder url = new StringBuilder(endpoint);
						url.Append("?lang=").Append(Uri.EscapeDataString(lang));
						url.Append("&srv=browser_video_translation");
						url.Append("&format=text");
						try
						{
							HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url.ToString());
							try
							{
								((HttpHeaders)request.Headers).TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
								request.Content = (HttpContent)new FormUrlEncodedContent((IEnumerable<KeyValuePair<string, string>>)new KeyValuePair<string, string>[1]
								{
									new KeyValuePair<string, string>("text", text)
								});
								HttpResponseMessage response = await ((HttpMessageInvoker)client).SendAsync(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								try
								{
									statusCodes[i] = (int)response.StatusCode;
									lastHttp = "HTTP " + (int)response.StatusCode;
									string body = await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
									if (!response.IsSuccessStatusCode)
									{
										goto end_IL_025c;
									}
									List<string> translatedTexts = ExtractTextArray(body);
									slots[i] = ((translatedTexts.Count < 1) ? "" : WebUtility.HtmlDecode(translatedTexts[0]));
									if (!string.IsNullOrWhiteSpace(slots[i]))
									{
										break;
									}
									goto end_IL_0132;
									end_IL_025c:;
								}
								finally
								{
									((IDisposable)response)?.Dispose();
								}
							}
							finally
							{
								((IDisposable)request)?.Dispose();
							}
							end_IL_0132:;
						}
						catch (OperationCanceledException)
						{
							if (cancellationToken.IsCancellationRequested)
							{
								throw;
							}
							lastHttp = "timeout";
						}
						catch (Exception ex)
						{
							lastHttp = ex.GetType().Name;
						}
					}
					if (string.IsNullOrWhiteSpace(slots[i]))
					{
						errorSlots[i] = (string.IsNullOrEmpty(lastHttp) ? "no response" : lastHttp);
						Interlocked.Increment(ref failedItems);
					}
				}
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			for (int j = 0; j < texts.Count; j++)
			{
				result.CharacterCount += (texts[j] ?? "").Length;
				result.Translations.Add(slots[j] ?? "");
			}
			int failedHttpStatus = 0;
			int firstFailedStatus = 0;
			string detail = "";
			for (int k = 0; k < errorSlots.Length; k++)
			{
				if (!string.IsNullOrWhiteSpace(errorSlots[k]))
				{
					if (string.IsNullOrWhiteSpace(detail))
					{
						detail = errorSlots[k];
					}
					int code = statusCodes[k];
					if (firstFailedStatus == 0 && code != 0)
					{
						firstFailedStatus = code;
					}
					if (failedHttpStatus == 0 && code != 0 && (code < 200 || code >= 300))
					{
						failedHttpStatus = code;
					}
				}
			}
			result.StatusCode = ((failedHttpStatus != 0) ? failedHttpStatus : firstFailedStatus);
			result.Success = result.Translations.Exists((string t) => !string.IsNullOrWhiteSpace(t));
			if (failedItems > 0 || !result.Success)
			{
				result.Error = "Yandex failed for " + failedItems + "/" + texts.Count + " items. " + (string.IsNullOrWhiteSpace(detail) ? "no usable translations" : detail);
			}
			return result;
		}

		private static List<string> ExtractTextArray(string json)
		{
			List<string> list = new List<string>();
			if (string.IsNullOrEmpty(json))
			{
				return list;
			}
			int num = json.IndexOf("\"text\"", StringComparison.OrdinalIgnoreCase);
			if (num < 0)
			{
				return list;
			}
			int num2 = json.IndexOf('[', num);
			if (num2 < 0)
			{
				return list;
			}
			bool flag = false;
			bool flag2 = false;
			StringBuilder stringBuilder = new StringBuilder();
			for (int i = num2 + 1; i < json.Length; i++)
			{
				char c = json[i];
				if (!flag)
				{
					switch (c)
					{
					case '"':
						flag = true;
						stringBuilder.Length = 0;
						continue;
					default:
						continue;
					case ']':
						break;
					}
					break;
				}
				if (flag2)
				{
					switch (c)
					{
					case '"':
						stringBuilder.Append('"');
						break;
					case '\\':
						stringBuilder.Append('\\');
						break;
					case 'n':
						stringBuilder.Append('\n');
						break;
					case 'r':
						stringBuilder.Append('\r');
						break;
					case 't':
						stringBuilder.Append('\t');
						break;
					case 'b':
						stringBuilder.Append('\b');
						break;
					case 'f':
						stringBuilder.Append('\f');
						break;
					case 'u':
						if (i + 4 < json.Length)
						{
							string s = json.Substring(i + 1, 4);
							if (ushort.TryParse(s, NumberStyles.HexNumber, null, out var result))
							{
								stringBuilder.Append((char)result);
								i += 4;
							}
							else
							{
								stringBuilder.Append('u');
							}
						}
						else
						{
							stringBuilder.Append('u');
						}
						break;
					default:
						stringBuilder.Append(c);
						break;
					}
					flag2 = false;
				}
				else
				{
					switch (c)
					{
					case '\\':
						flag2 = true;
						break;
					case '"':
						list.Add(stringBuilder.ToString());
						flag = false;
						break;
					default:
						stringBuilder.Append(c);
						break;
					}
				}
			}
			return list;
		}

		private static string Code(string language)
		{
			switch ((language ?? "").Trim().ToLowerInvariant())
			{
			case "english":
				return "en";
			case "czech":
				return "cs";
			case "german":
				return "de";
			case "french":
				return "fr";
			case "italian":
				return "it";
			case "japanese":
				return "ja";
			case "korean":
				return "ko";
			case "polish":
				return "pl";
			case "portuguese":
			case "portuguesebrazilian":
				return "pt";
			case "russian":
				return "ru";
			case "spanish":
				return "es";
			case "dutch":
				return "nl";
			case "danish":
				return "da";
			case "swedish":
				return "sv";
			case "norwegian":
				return "no";
			case "ukrainian":
				return "uk";
			case "chinesesimplified":
			case "chinesetraditional":
			case "chinese":
				return "zh";
			case "turkish":
				return "tr";
			case "vietnamese":
				return "vi";
			case "arabic":
				return "ar";
			case "bulgarian":
				return "bg";
			case "croatian":
				return "hr";
			case "finnish":
				return "fi";
			case "greek":
				return "el";
			case "hebrew":
				return "he";
			case "hungarian":
				return "hu";
			case "indonesian":
				return "id";
			case "romanian":
				return "ro";
			case "slovak":
				return "sk";
			case "slovenian":
				return "sl";
			case "thai":
				return "th";
			case "estonian":
				return "et";
			case "latvian":
				return "lv";
			case "lithuanian":
				return "lt";
			default:
				return (language ?? "").Trim().ToLowerInvariant();
			}
		}
	}
}
