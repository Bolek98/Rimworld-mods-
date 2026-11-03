using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModSortManager
{
    // ===========================================================================
    // 0.7.0: own names for mods (an idea from a tester). Right-click a mod → Rename… The nickname is
    // only for the eyes: it shows in Mod Sort Manager and in the game's own mod list, while the real
    // name stays everywhere the game or other mods work with it (ModContentPack.Name, saves, mod
    // lists, the Workshop upload, other mods checking "is mod X running"). Saved by packageId.
    // ===========================================================================
    public static class Nicknames
    {
        private static Dictionary<string, string> map;

        private static Dictionary<string, string> Map
        {
            get
            {
                if (map != null) return map;
                var d = new Dictionary<string, string>();
                foreach (string line in MSMMod.Settings?.nicknames ?? new List<string>())
                {
                    int i = line.IndexOf('|');
                    if (i <= 0 || i >= line.Length - 1) continue;
                    d[line.Substring(0, i)] = line.Substring(i + 1);
                }
                map = d;
                return map;
            }
        }

        private static string Key(ModMetaData m) => (m?.PackageId ?? "").ToLowerInvariant();

        public static string Of(ModMetaData m)
        {
            if (m == null) return null;
            return Map.TryGetValue(Key(m), out string n) ? n : null;
        }

        public static bool Has(ModMetaData m) => Of(m) != null;

        // the name to draw: the nickname when there is one and they are switched on
        public static string Show(ModMetaData m)
        {
            if (m == null) return "";
            string orig = m.Name ?? "";
            if (MSMMod.Settings == null || !MSMMod.Settings.showNicknames) return orig;
            return Of(m) ?? orig;
        }

        public static bool Shown(ModMetaData m) => MSMMod.Settings != null && MSMMod.Settings.showNicknames && Has(m);

        // a line for tooltips and the details: "Original: …"
        public static string OriginalLine(ModMetaData m)
        {
            if (!Shown(m) || !MSMMod.Settings.showOriginalName) return "";
            return "MSM_NickOriginal".Translate(m.Name).ToString();
        }

        public static void Set(ModMetaData m, string nick)
        {
            if (m == null || MSMMod.Settings == null) return;
            string k = Key(m);
            nick = (nick ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();
            var list = MSMMod.Settings.nicknames;
            list.RemoveAll(l => l.StartsWith(k + "|", StringComparison.Ordinal));
            if (nick.Length > 0 && nick != m.Name) list.Add(k + "|" + nick);
            map = null;
            MSMMod.Settings.Write();
        }

        public static void Rename(ModMetaData m, Action after = null)
        {
            if (m == null) return;
            Find.WindowStack.Add(new Dialog_ModNick(m, after));
        }

        // search matches the nickname and the real name
        public static bool Matches(ModMetaData m, string q)
        {
            if ((m.Name ?? "").ToLowerInvariant().Contains(q)) return true;
            string n = Of(m);
            return n != null && n.ToLowerInvariant().Contains(q);
        }

        public static string Mark(ModMetaData m) => Shown(m) ? "✎ " : "";

        // 0.8.2: the name of a mod's entry in Options → Mod settings (and the title of its settings window)
        private static readonly Dictionary<Mod, string> catCache = new Dictionary<Mod, string>();
        private static bool catFailed;

        public static string Category(Mod mod)
        {
            string orig = mod?.SettingsCategory();
            try
            {
                if (orig.NullOrEmpty() || MSMMod.Settings == null || !MSMMod.Settings.showNicknames) return orig;
                ModMetaData meta = mod.Content?.ModMetaData;
                string nick = Of(meta);
                if (nick == null) return orig;
                // a mod with several settings pages: each keeps its own name in brackets
                int pages = 0;
                foreach (Mod h in LoadedModManager.ModHandles)
                    if (h.Content == mod.Content && !h.SettingsCategory().NullOrEmpty()) pages++;
                return pages > 1 && orig != nick ? nick + " (" + orig + ")" : nick;
            }
            catch (Exception e)
            {
                if (!catFailed) { catFailed = true; Log.Warning("[Mod Sort Manager] nickname in mod settings: " + e.Message); }
                return orig;
            }
        }
    }

    public class Dialog_ModNick : Window
    {
        private readonly ModMetaData mod;
        private readonly Action after;
        private string text;
        private bool focused;

        public Dialog_ModNick(ModMetaData mod, Action after)
        {
            this.mod = mod;
            this.after = after;
            text = Nicknames.Of(mod) ?? mod.Name ?? "";
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(460f, 200f);

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(r.x, r.y, r.width, 26f), "MSM_NickTitle".Translate());
            GUI.color = new Color(0.7f, 0.72f, 0.75f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x, r.y + 24f, r.width, 20f), "MSM_NickOriginal".Translate(mod.Name).ToString().Truncate(r.width));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            GUI.SetNextControlName("MSMNick");
            text = Widgets.TextField(new Rect(r.x, r.y + 50f, r.width, 32f), text, 80);
            if (!focused) { UI.FocusControl("MSMNick", this); focused = true; }
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (Widgets.ButtonText(new Rect(r.xMax - 120f, r.yMax - 36f, 120f, 34f), "OK".Translate()) || enter)
            {
                Nicknames.Set(mod, text);
                after?.Invoke();
                if (enter) Event.current.Use();
                Close();
            }
            if (Widgets.ButtonText(new Rect(r.x, r.yMax - 36f, 170f, 34f), "MSM_NickReset".Translate()))
            {
                Nicknames.Set(mod, null);
                after?.Invoke();
                Close();
            }
            if (Widgets.ButtonText(new Rect(r.xMax - 250f, r.yMax - 36f, 120f, 34f), "CancelButton".Translate())) Close();
        }
    }

    // The game's own mod list (Page_ModsConfig): every read of ModMetaData.Name in its methods and in
    // their lambdas shows the nickname instead – except saving a mod list, which keeps the real names.
    [HarmonyPatch]
    public static class Patch_VanillaModListNames
    {
        private static readonly MethodInfo getName = AccessTools.PropertyGetter(typeof(ModMetaData), nameof(ModMetaData.Name));
        private static readonly MethodInfo show = AccessTools.Method(typeof(Nicknames), nameof(Nicknames.Show));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            var types = new List<Type> { typeof(Page_ModsConfig) };
            types.AddRange(typeof(Page_ModsConfig).GetNestedTypes(AccessTools.all));
            foreach (Type t in types)
                foreach (MethodInfo m in t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.Name.Contains("SaveModList")) continue;
                    if (m.DeclaringType != t) continue;
                    MethodBody body;
                    try { body = m.GetMethodBody(); } catch { continue; }
                    if (body == null) continue;
                    if (UsesName(m)) yield return m;
                }
        }

        private static bool UsesName(MethodInfo m)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => p.Key == OpCodes.Callvirt || p.Key == OpCodes.Call ? p.Value as MethodInfo == getName : false);
            }
            catch { return false; }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand as MethodInfo == getName)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = show;
                }
                yield return ci;
            }
        }
    }

    // 0.8.2: Options → Mod settings list and the mod settings window title show the nickname;
    // SettingsCategory itself is not touched (other mods use it as a key)
    [HarmonyPatch]
    public static class Patch_ModSettingsNames
    {
        private static readonly MethodInfo cat = AccessTools.Method(typeof(Mod), nameof(Mod.SettingsCategory));
        private static readonly MethodInfo show = AccessTools.Method(typeof(Nicknames), nameof(Nicknames.Category));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo a = AccessTools.Method(typeof(Dialog_Options), "DoModOptions");
            if (a != null) yield return a;
            MethodInfo b = AccessTools.Method(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents));
            if (b != null) yield return b;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand as MethodInfo == cat)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = show;
                }
                yield return ci;
            }
        }
    }
}
