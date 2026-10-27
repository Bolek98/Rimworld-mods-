using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using RimWorld;
using UnityEngine;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    // 2.6.0: tools of the Overview tab
    //  * "Translate everything now" – one click: retry what failed, forget machine translations
    //    that failed the checks (the player's own stay), let waiting new mods in, scan all mods
    //    again (scan cache ignored) and translate what is missing; progress and a message when done.
    //  * diagnostics – how many texts wait, how many failed and why, which services do not work.
    //  * import from other translator mods – Auto Translation (seohyeon.autotranslation) keeps its
    //    translations in <save data>/AutoTranslation/*.xml (root <Cache Language="Czech">, entries
    //    <Entry Key="base64 of modId:original[keyed key]">translation</Entry>, or older entries whose
    //    element name is the key); and a plain file Config/urt_import_<lang>.tsv
    //    (source<TAB>translation, \t \n escaped). Only entries for the current language are taken,
    //    the player's own translations win and working translations are never replaced.
    //  * translation packs – the memory (or only the texts of chosen mods) saved as a .tsv file in
    //    Config/UniversalRimTranslator2/packs, and packs from there taken in (same rules as import).
    public static class UrtTools
    {
        // ---------------- translate everything now ----------------
        public static volatile bool AllRunning;
        private static int allStartTranslated;
        private static float allStartTime;
        private static bool allSawWork;
        private static float lastTick, scanEndedAt = -1f;

        public static void ConfirmAll()
        {
            if (!Runtime.Active || Runtime.Store == null) return;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("URT_All_Confirm".Translate(), StartAll, false));
        }

        public static void StartAll()
        {
            try
            {
                Runtime.RetryFailed();
                int forgot = Runtime.Store.ForgetFailedAuto();
                if (Runtime.HeldMods != null && Runtime.HeldMods.Count > 0) Runtime.AllowNewMods();
                Runtime.Paused = false;
                Runtime.Rescan();
                AllRunning = true;
                allSawWork = false;
                scanEndedAt = -1f;
                allStartTranslated = Runtime.Translated;
                allStartTime = Time.realtimeSinceStartup;
                diagAt = -100f;
                MLog.Info("[URT] translate everything now: forgot " + forgot + " failed machine translations, fresh scan started");
                Messages.Message("URT_All_Started".Translate(forgot), MessageTypeDefOf.NeutralEvent, false);
                Runtime.Kick();
            }
            catch (Exception e)
            {
                AllRunning = false;
                MLog.Warn("[URT] translate everything now: " + e.Message);
            }
        }

        // main thread, every frame (cheap)
        public static void Tick()
        {
            if (!AllRunning) return;
            float now = Time.realtimeSinceStartup;
            if (now - lastTick < 1f) return;
            lastTick = now;
            if (Runtime.Scanning) return;
            if (scanEndedAt < 0f) scanEndedAt = now;
            if (now - scanEndedAt < 3f) return;     // the translator thread first looks at the new scan
            if (Runtime.PendingCached > 0) allSawWork = true;
            // done: the scan finished and the translator thread found nothing left
            if (Runtime.Phase != null && Runtime.Phase.StartsWith("done", StringComparison.Ordinal))
            {
                AllRunning = false;
                Messages.Message("URT_All_Done".Translate(Runtime.Translated - allStartTranslated), MessageTypeDefOf.PositiveEvent, false);
            }
            else if (UniversalRimTranslatorMod.Settings.offlineOnly || (Runtime.Phase ?? "").StartsWith("waiting", StringComparison.Ordinal) && !Runtime.Chain.AnyAvailable() && allSawWork && now - allStartTime > 60f)
            {
                // nothing can be translated now (offline or no working service) – stop the run, the diagnostics say why
                AllRunning = false;
                Messages.Message("URT_All_Stopped".Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }

        public static string ProgressLine()
        {
            if (!AllRunning) return null;
            if (Runtime.Scanning) return "URT_All_Scanning".Translate();
            float min = Mathf.Max(1f / 60f, (Time.realtimeSinceStartup - allStartTime) / 60f);
            int perMin = Mathf.RoundToInt((Runtime.Translated - allStartTranslated) / min);
            string svc = Runtime.Chain.LastProvider ?? "–";
            return "URT_All_Progress".Translate(Runtime.PendingCached, perMin, svc);
        }

        // ---------------- diagnostics ----------------
        private static float diagAt = -100f;
        private static string diagText;

        public static string Diagnostics()
        {
            float now = Time.realtimeSinceStartup;
            if (diagText != null && now - diagAt < 3f) return diagText;
            diagAt = now;
            var lines = new List<string>();
            try
            {
                int failed = 0, rejected = 0;
                foreach (int g in Runtime.GiveUpValues())
                {
                    if (g >= 99) rejected++;
                    else if (g >= 3) failed++;
                }
                int stored = Runtime.Store?.CountFailedAuto() ?? 0;
                lines.Add("URT_Diag_Line".Translate(Runtime.PendingCached, failed, Math.Max(rejected, stored)));
                if (UniversalRimTranslatorMod.Settings.offlineOnly)
                    lines.Add("URT_Diag_Offline".Translate());
                else if (!Runtime.Chain.AnyAvailable())
                    lines.Add("URT_Diag_NoService".Translate());
                var probs = Runtime.Chain.Problems();
                if (probs.Count > 0)
                {
                    var parts = probs.Select(p => p.KeyRejected
                        ? (string)"URT_Diag_KeyRejected".Translate(p.Name)
                        : (string)"URT_Diag_Paused".Translate(p.Name, p.Seconds)).ToArray();
                    lines.Add("URT_Diag_Services".Translate(string.Join("; ", parts)));
                }
            }
            catch (Exception e) { lines.Add(e.Message); }
            diagText = string.Join("\n", lines.ToArray());
            return diagText;
        }

        // ---------------- import from other translators ----------------
        private static volatile bool busy;
        public static bool Busy => busy;
        public static volatile string Note;

        public static string TsvImportPath => Path.Combine(GenFilePaths.ConfigFolderPath, "urt_import_" + Runtime.Target + ".tsv");

        public static void ImportOthers()
        {
            if (busy || !Runtime.Active || Runtime.Store == null) return;
            busy = true;
            Note = "URT_Import_Working".Translate();
            string target = Runtime.Target;
            new Thread(() =>
            {
                var report = new List<string>();
                int taken = 0, skipped = 0;
                try
                {
                    // Auto Translation (seohyeon.autotranslation)
                    string atDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "AutoTranslation");
                    if (Directory.Exists(atDir))
                        foreach (string f in Directory.GetFiles(atDir, "*.xml"))
                        {
                            ImportAutoTranslation(f, target, out int t, out int s);
                            if (t + s > 0) report.Add("Auto Translation " + Path.GetFileName(f) + ": " + t);
                            taken += t;
                            skipped += s;
                        }
                    // the plain file
                    foreach (string f in new[] { TsvImportPath, Path.Combine(GenFilePaths.ConfigFolderPath, "urt_import_" + LanguageDatabase.activeLanguage?.folderName + ".tsv") }.Distinct())
                        if (File.Exists(f))
                        {
                            ImportTsv(f, target, "import:tsv", out int t, out int s, out _);
                            report.Add(Path.GetFileName(f) + ": " + t);
                            taken += t;
                            skipped += s;
                        }
                    int tk = taken, sk = skipped;
                    string list = string.Join(", ", report.ToArray());
                    MLog.Info("[URT] import: taken " + tk + ", skipped " + sk + " (" + list + ")");
                    Finish(() => report.Count == 0
                        ? (string)"URT_Import_None".Translate(Path.GetFileName(TsvImportPath))
                        : (string)"URT_Import_Done".Translate(tk, list, sk), tk > 0);
                }
                catch (Exception e)
                {
                    MLog.Warn("[URT] import: " + e);
                    Finish(() => e.Message, taken > 0);
                }
            }) { IsBackground = true, Name = "URT import" }.Start();
        }

        // the message is made on the main thread (translation lookups)
        private static void Finish(Func<string> make, bool changed)
        {
            Runtime.RunOnMain(() =>
            {
                busy = false;
                string msg;
                try { msg = make(); } catch (Exception e) { msg = e.Message; }
                Note = msg;
                Messages.Message(msg, MessageTypeDefOf.NeutralEvent, false);
                if (changed)
                {
                    if (Runtime.Store.JournalBig) Runtime.Store.Compact();
                    Runtime.Reapply();
                    Runtime.Kick();
                }
            });
        }

        // Auto Translation's cache: only files saved for the current language
        private static void ImportAutoTranslation(string path, string target, out int taken, out int skipped)
        {
            taken = skipped = 0;
            var doc = new XmlDocument();
            doc.Load(path);
            XmlElement root = doc.DocumentElement;
            if (root == null) return;
            string lang = root.GetAttribute("Language");
            if (string.IsNullOrEmpty(lang) || lang == "NULL" || !Lang.Same(lang, target)) return;
            Dictionary<string, LoadedLanguage.KeyedReplacement> keyed = LanguageDatabase.defaultLanguage?.keyedReplacements;
            foreach (XmlNode n in root.ChildNodes)
            {
                if (!(n is XmlElement e)) continue;
                string key = e.HasAttribute("Key") ? FromBase64(e.GetAttribute("Key")) : XmlConvert.DecodeName(e.Name);
                string value = e.InnerText;
                if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(value)) continue;
                // "packageId:original" (newer versions) – the id never has a ':'
                int colon = key.IndexOf(':');
                if (colon > 0 && LooksLikeId(key.Substring(0, colon))) key = key.Substring(colon + 1);
                string source = Resolve(key, keyed);
                if (source == null) { skipped++; continue; }
                int r = Runtime.Store.PutImported(source, value, "import:autotranslation");
                if (r > 0) taken++; else skipped++;
            }
        }

        private static bool LooksLikeId(string s)
        {
            if (s.Length == 0 || s.Length > 120) return false;
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')) return false;
            return true;
        }

        private static string FromBase64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return s; }
        }

        // the original text as URT knows it; Keyed entries may carry the key name at the end
        private static string Resolve(string text, Dictionary<string, LoadedLanguage.KeyedReplacement> keyed)
        {
            string t = text.Replace("\r\n", "\n").Trim();
            if (Runtime.KnownSource(Store.Key(t))) return t;
            if (keyed != null)
                for (int i = t.Length - 1; i > 0; i--)
                {
                    char c = t[i];
                    if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) break;
                    string name = t.Substring(i);
                    if (keyed.ContainsKey(name))
                    {
                        string src = t.Substring(0, i).Trim();
                        if (Runtime.KnownSource(Store.Key(src))) return src;
                    }
                }
            return null;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");

        // source<TAB>translation per line; a first line "#URT pack<TAB>cs<TAB>…" names the language
        private static void ImportTsv(string path, string target, string provider, out int taken, out int skipped, out string packLang)
        {
            taken = skipped = 0;
            packLang = null;
            bool first = true;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (first && line.StartsWith("#URT", StringComparison.Ordinal))
                {
                    string[] h = line.Split('\t');
                    packLang = h.Length > 1 ? h[1].Trim() : null;
                    if (!string.IsNullOrEmpty(packLang) && !Lang.Same(packLang, target)) return;
                    first = false;
                    continue;
                }
                first = false;
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) { skipped++; continue; }
                string src = Unescape(line.Substring(0, tab));
                string tr = Unescape(line.Substring(tab + 1));
                int r = Runtime.Store.PutImported(src, tr, provider);
                if (r > 0) taken++; else skipped++;
            }
        }

        // ---------------- translation packs ----------------
        public static string PacksFolder => Path.Combine(Runtime.Folder, "packs");

        public static void OpenPacksFolder()
        {
            try
            {
                Directory.CreateDirectory(PacksFolder);
                Application.OpenURL("file:///" + PacksFolder.Replace('\\', '/'));
            }
            catch (Exception e) { MLog.Warn("[URT] open packs folder: " + e.Message); }
        }

        // mods == null: the whole memory; otherwise only texts of these mods (as in Item.Mod)
        public static void ExportPack(string name, HashSet<string> mods)
        {
            if (busy || Runtime.Store == null) return;
            busy = true;
            Note = "URT_Pack_Working".Translate();
            List<Item> items = Runtime.Items;
            Store store = Runtime.Store;
            string target = Runtime.Target;
            new Thread(() =>
            {
                try
                {
                    var keys = new List<string>();
                    if (mods == null)
                    {
                        foreach (var kv in store.Snapshot())
                            if (kv.Value != null && kv.Value.Usable) keys.Add(kv.Key);
                    }
                    else
                    {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (Item it in items)
                            if (it?.Mod != null && mods.Contains(it.Mod))
                            {
                                string k = Store.Key(it.Source);
                                if (seen.Add(k)) keys.Add(k);
                            }
                    }
                    Directory.CreateDirectory(PacksFolder);
                    string file = Path.Combine(PacksFolder, SafeName(name) + "_" + target + ".tsv");
                    int n = 0;
                    using (var w = new StreamWriter(file, false, new UTF8Encoding(false)))
                    {
                        w.WriteLine("#URT pack\t" + target + "\t" + DateTime.Now.ToString("s") + "\tUniversal RimTranslator " + UrtVersion.Number);
                        foreach (string k in keys)
                        {
                            if (!store.TryGet(k, out Rec r) || r == null || !r.Usable) continue;
                            w.WriteLine(Escape(k) + "\t" + Escape(r.Text));
                            n++;
                        }
                    }
                    int count = n;
                    MLog.Info("[URT] pack exported: " + count + " translations to " + file);
                    Finish(() => "URT_Pack_Exported".Translate(count, file), false);
                }
                catch (Exception e)
                {
                    MLog.Warn("[URT] export pack: " + e);
                    Finish(() => e.Message, false);
                }
            }) { IsBackground = true, Name = "URT pack export" }.Start();
        }

        public static void ImportPack(string file)
        {
            if (busy || Runtime.Store == null) return;
            busy = true;
            Note = "URT_Pack_Working".Translate();
            string target = Runtime.Target;
            new Thread(() =>
            {
                try
                {
                    ImportTsv(file, target, "pack:" + Path.GetFileNameWithoutExtension(file), out int t, out int s, out string lang);
                    bool wrong = !string.IsNullOrEmpty(lang) && !Lang.Same(lang, target);
                    MLog.Info("[URT] pack " + file + ": taken " + t + ", skipped " + s + (wrong ? ", other language " + lang : ""));
                    Finish(() => wrong
                        ? (string)"URT_Pack_WrongLang".Translate(lang, target)
                        : (string)"URT_Import_Done".Translate(t, Path.GetFileName(file), s), t > 0);
                }
                catch (Exception e)
                {
                    MLog.Warn("[URT] import pack: " + e);
                    Finish(() => e.Message, false);
                }
            }) { IsBackground = true, Name = "URT pack import" }.Start();
        }

        public static void ImportPackMenu()
        {
            var opts = new List<FloatMenuOption>();
            try
            {
                if (Directory.Exists(PacksFolder))
                    foreach (string f in Directory.GetFiles(PacksFolder, "*.tsv").OrderBy(x => x))
                    {
                        string file = f;
                        opts.Add(new FloatMenuOption(Path.GetFileName(file), () => ImportPack(file)));
                    }
            }
            catch (Exception e) { MLog.Warn("[URT] packs: " + e.Message); }
            if (opts.Count == 0)
            {
                Messages.Message("URT_Pack_NoFiles".Translate(PacksFolder), MessageTypeDefOf.RejectInput, false);
                OpenPacksFolder();
                return;
            }
            opts.Add(new FloatMenuOption("URT_Pack_OpenFolder".Translate(), OpenPacksFolder));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static string SafeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) s = "pack";
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
                sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return sb.ToString();
        }
    }

    // 2.6.0: export a translation pack – the whole memory or only chosen mods
    public class Dialog_UrtPack : Window
    {
        private readonly List<string> mods;
        private readonly HashSet<string> chosen = new HashSet<string>(StringComparer.Ordinal);
        private bool all = true;
        private string name = "my_translation";
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(620f, 640f);

        public Dialog_UrtPack()
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            mods = Runtime.Items.Where(i => i?.Mod != null).Select(i => i.Mod).Distinct().OrderBy(m => m).ToList();
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 34f), "URT_Pack_Title".Translate());
            Text.Font = GameFont.Small;
            float y = r.y + 40f;
            Widgets.Label(new Rect(r.x, y, 140f, 28f), "URT_Pack_Name".Translate());
            name = Widgets.TextField(new Rect(r.x + 145f, y, r.width - 145f, 28f), name ?? "");
            y += 36f;
            bool a = all;
            Widgets.CheckboxLabeled(new Rect(r.x, y, r.width, 26f), "URT_Pack_All".Translate(), ref a);
            all = a;
            y += 30f;
            Widgets.Label(new Rect(r.x, y, r.width, 26f), "URT_Pack_Mods".Translate());
            y += 28f;
            Rect outer = new Rect(r.x, y, r.width, r.yMax - y - 46f);
            Rect view = new Rect(0f, 0f, outer.width - 16f, mods.Count * 26f);
            if (all) GUI.color = new Color(1f, 1f, 1f, 0.45f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            for (int i = 0; i < mods.Count; i++)
            {
                bool on = chosen.Contains(mods[i]);
                bool was = on;
                Widgets.CheckboxLabeled(new Rect(0f, i * 26f, view.width, 24f), mods[i], ref on);
                if (on != was && !all)
                {
                    if (on) chosen.Add(mods[i]); else chosen.Remove(mods[i]);
                }
            }
            Widgets.EndScrollView();
            GUI.color = Color.white;
            bool can = !UrtTools.Busy && (all || chosen.Count > 0);
            if (Widgets.ButtonText(new Rect(r.xMax - 200f, r.yMax - 38f, 200f, 36f), "URT_Pack_DoExport".Translate(), active: can) && can)
            {
                UrtTools.ExportPack(name, all ? null : new HashSet<string>(chosen, StringComparer.Ordinal));
                Close();
            }
            if (Widgets.ButtonText(new Rect(r.x, r.yMax - 38f, 220f, 36f), "URT_Pack_OpenFolder".Translate()))
                UrtTools.OpenPacksFolder();
        }
    }
}
