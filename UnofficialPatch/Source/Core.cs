using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace UnofficialPatch
{
    // ---------------------------------------------------------------------------
    // Unofficial 1.7 Patch: fixes for problems of the vanilla game in the RimWorld 1.7
    // (Waylith) beta, until Ludeon fixes them in an official update.
    //
    // Every fix knows the exact game code it was written for (a fingerprint of the method
    // bodies, see ILSig). When an update changes that code – most likely because Ludeon fixed
    // it – the fix turns itself off and the settings say so. Nothing ever patches code it was
    // not made for. Every fix can also be turned off by hand.
    // ---------------------------------------------------------------------------
    public class PatchSettings : ModSettings
    {
        public Dictionary<string, bool> off = new Dictionary<string, bool>();

        public bool IsOn(string id) => !(off.TryGetValue(id, out bool o) && o);

        public override void ExposeData()
        {
            List<string> keys = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                keys = new List<string>();
                foreach (var kv in off)
                    if (kv.Value) keys.Add(kv.Key);
            }
            Scribe_Collections.Look(ref keys, "turnedOff", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                off = new Dictionary<string, bool>();
                if (keys != null)
                    foreach (string k in keys) off[k] = true;
            }
        }
    }

    public enum FixState { Active, OffByUser, GameChanged, Missing, Failed }

    // One fix: the vanilla methods it relies on (with their fingerprints) and how it patches.
    public class Fix
    {
        public string id;
        public string group;          // "perf" or "bug"
        public string credit;         // who found it
        public (string type, string method, uint sig)[] needs;
        public Action<Harmony> apply;
        public FixState state;
        public string detail;
        public static bool runtimeOn = true;

        public static Fix Get(string id) => UnofficialPatchMod.Fixes.Find(f => f.id == id);
    }

    public class UnofficialPatchMod : Mod
    {
        public const string Version = "0.1.1";
        public const string TestedBuild = "1.7.4985";
        public static PatchSettings Settings;
        public static readonly List<Fix> Fixes = new List<Fix>();

        // fast switches read by the patches every call (false = behave exactly like vanilla)
        public static bool OnWind, OnFreeze, OnGas, OnSealed, OnWorld, OnHealth, OnSteal, OnCaravan, OnWanderers;

        public UnofficialPatchMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PatchSettings>();
            MNews.Register(content, UnofficialPatchMod.Version);   // what's new + Bolek98 overview
            MUI.Init(new PatchSettings(), null);
            var h = new Harmony("bolek98.unofficialpatch");
            try { MUI.PatchWindow(h, typeof(UnofficialPatchMod)); } catch { }
            Define();
            int active = 0;
            foreach (Fix f in Fixes)
            {
                Apply(f, h);
                if (f.state == FixState.Active) active++;
            }
            Refresh();
            var sb = new System.Text.StringBuilder();
            foreach (Fix f in Fixes)
                if (f.state != FixState.Active && f.state != FixState.OffByUser)
                    sb.Append(" " + f.id + "=" + f.state + (f.detail != null ? " (" + f.detail + ")" : "") + ";");
            Log.Message("[Unofficial 1.7 Patch] v" + Version + " loaded – " + active + " of " + Fixes.Count + " fixes active." + (sb.Length > 0 ? " Paused:" + sb : ""));
        }

        private static void Apply(Fix f, Harmony h)
        {
            if (!Settings.IsOn(f.id))
            {
                f.state = FixState.OffByUser;
                return;
            }
            foreach (var n in f.needs)
            {
                Type t = AccessTools.TypeByName(n.type);
                MethodInfo m = null;
                try { m = t == null ? null : AccessTools.DeclaredMethod(t, n.method); }
                catch (Exception e) { f.state = FixState.Failed; f.detail = n.method + ": " + e.Message; return; }
                if (m == null)
                {
                    f.state = FixState.Missing;
                    f.detail = n.type + "." + n.method;
                    return;
                }
                uint sig;
                try { sig = ILSig.Of(m); }
                catch (Exception e) { f.state = FixState.Failed; f.detail = e.Message; return; }
                if (sig != n.sig)
                {
                    f.state = FixState.GameChanged;
                    f.detail = t.Name + "." + n.method + " 0x" + sig.ToString("X8");
                    return;
                }
            }
            try
            {
                f.apply(h);
                f.state = FixState.Active;
            }
            catch (Exception e)
            {
                f.state = FixState.Failed;
                f.detail = e.Message;
                Log.Warning("[Unofficial 1.7 Patch] fix '" + f.id + "' could not be applied and stays off: " + e);
            }
        }

        // The switches: a fix patched at startup can be turned off at once (the patch then does
        // what vanilla does); turning one on that was off at startup needs a restart.
        public static void Refresh()
        {
            bool On(string id) { Fix f = Fix.Get(id); return f != null && f.state == FixState.Active && Settings.IsOn(id); }
            OnWind = On("wind");
            OnFreeze = On("freeze");
            OnGas = On("gas");
            OnSealed = On("sealed");
            OnWorld = On("world");
            OnHealth = On("health");
            OnSteal = On("steal");
            OnCaravan = On("caravan");
            OnWanderers = On("wanderers");
        }

        private static void Define()
        {
            Fixes.Add(new Fix
            {
                id = "wind", group = "perf", credit = "Dreadarm",
                needs = new[] { ("Verse.WindManager", "WindManagerTick", 0x6D17431Eu) },
                apply = Perf.PatchWind
            });
            Fixes.Add(new Fix
            {
                id = "freeze", group = "perf", credit = "Dreadarm",
                needs = new[]
                {
                    ("Verse.FreezeManager", "DoCellSteadyEffects", 0xFB2F4B2Cu),
                    ("Verse.FreezeManager", "DoIceMelting", 0xD22EFFDCu),
                    ("Verse.FreezeManager", "DoWaterFreezing", 0x2A727E2Eu)
                },
                apply = Perf.PatchFreeze
            });
            Fixes.Add(new Fix
            {
                id = "gas", group = "perf", credit = "Dreadarm",
                needs = new[]
                {
                    ("Verse.GasGrid", "Tick", 0x405F8E43u),
                    ("Verse.GasGrid", "TryDissipateGases", 0xD08893ACu),
                    ("Verse.GasGrid", "TryDiffuseGases", 0xD100040Cu)
                },
                apply = Perf.PatchGas
            });
            Fixes.Add(new Fix
            {
                id = "sealed", group = "perf", credit = "Dreadarm",
                needs = new[] { ("Verse.GenTypes", "IsSealedWithCache", 0xBF99089Du) },
                apply = Perf.PatchSealed
            });
            Fixes.Add(new Fix
            {
                id = "world", group = "perf", credit = "Dreadarm",
                needs = new[]
                {
                    ("RimWorld.Planet.WorldObject", "DoTick", 0x9ECA8BBFu),
                    ("RimWorld.Planet.Settlement", "GetChildHolders", 0x92D7AA19u),
                    ("RimWorld.Planet.MapParent", "GetChildHolders", 0x6297C70Bu)
                },
                apply = Perf.PatchWorld
            });
            Fixes.Add(new Fix
            {
                id = "health", group = "perf", credit = "Dreadarm",
                needs = new[] { ("Verse.Pawn_HealthTracker", "HealthTick", 0xAC8023FFu) },
                apply = Perf.PatchHealth
            });
            Fixes.Add(new Fix
            {
                id = "steal", group = "perf", credit = "mrbeverage",
                needs = new[] { ("RimWorld.StatWorker_MarketValue", "CalculableRecipe", 0x352F73F5u) },
                apply = Perf.PatchSteal
            });
            Fixes.Add(new Fix
            {
                id = "caravan", group = "bug", credit = "Bolek98",
                needs = new[] { ("RimWorld.Dialog_FormCaravan", "BestMedicineItemFor", 0x99598EF9u) },
                apply = Bugs.PatchCaravan
            });
            Fixes.Add(new Fix
            {
                id = "wanderers", group = "bug", credit = "ifforgotmysocks",
                needs = new[] { ("RimWorld.GameEnder", "CanSpawnNewWanderers", 0xA67A4DA9u) },
                apply = Bugs.PatchWanderers
            });
        }

        public override string SettingsCategory() => "Unofficial 1.7 Patch";

        private Vector2 scroll;

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            float y = r.y;
            string intro = "UP_Intro".Translate(TestedBuild, VersionControl.CurrentVersionStringWithRev);
            float ih = Text.CalcHeight(intro, r.width);
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, y, r.width, ih), intro);
            GUI.color = Color.white;
            y += ih + 6f;

            Rect outer = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, outer.width - 20f, 70f + Fixes.Count * 74f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            var l = new Listing_Standard();
            l.Begin(view);
            foreach (string group in new[] { "perf", "bug" })
            {
                MUI.Section(l, ("UP_Group_" + group).Translate());
                foreach (Fix f in Fixes)
                {
                    if (f.group != group) continue;
                    bool on = Settings.IsOn(f.id);
                    bool before = on;
                    MUI.Check(l, ("UP_" + f.id).Translate(), ("UP_" + f.id + "_Desc").Translate() + "\n\n" + "UP_Credit".Translate(f.credit), ref on, null, null);
                    if (on != before)
                    {
                        Settings.off[f.id] = !on;
                        Refresh();
                    }
                    Rect st = l.GetRect(20f);
                    GUI.color = StateColor(f, on);
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(st.x + 12f, st.y, st.width - 12f, st.height), StateText(f, on));
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    l.Gap(6f);
                }
            }
            l.End();
            Widgets.EndScrollView();
        }

        private static string StateText(Fix f, bool on)
        {
            if (!on) return "UP_State_OffUser".Translate();
            switch (f.state)
            {
                case FixState.Active: return "UP_State_Active".Translate();
                case FixState.OffByUser: return "UP_State_Restart".Translate();
                case FixState.GameChanged: return "UP_State_Changed".Translate(f.detail ?? "");
                case FixState.Missing: return "UP_State_Missing".Translate(f.detail ?? "");
                default: return "UP_State_Failed".Translate(f.detail ?? "");
            }
        }

        private static Color StateColor(Fix f, bool on)
        {
            if (on && f.state == FixState.Active) return MUI.Accent;
            if (!on) return MUI.Muted;
            return MUI.Warn;
        }
    }
}
