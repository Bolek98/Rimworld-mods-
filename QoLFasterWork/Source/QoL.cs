using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace QoLFW
{
    public enum Unwavering { Off, AfterDays, Always }

    public class QSettings : ModSettings
    {
        public bool recommended = true;

        // faster work
        public bool workSpeed = true;
        public bool colonistsOnly = true;
        public float spCraft = 1.5f, spBuild = 1.5f, spMine = 1.5f, spPlants = 1.5f, spResearch = 1.5f,
                     spCook = 1.5f, spClean = 1.5f, spMedical = 1.25f, spAnimals = 1.5f;

        // prisoners
        public bool prisoners = true;
        public float recruitMult = 2f, enslaveMult = 2f;
        public Unwavering unwavering = Unwavering.AfterDays;
        public int unwaveringDays = 15;
        public bool slaveRecruit = true;

        // tools
        public bool pipette = true;
        public bool pipetteInstant = false;
        public bool copyPawn = true;
        public bool haulNow = true;
        public bool zoneTools = true;
        public bool deconstructAll = true;
        public bool colonyTab = true;
        public bool stacks = false;
        public float stackMult = 2f;

        // notifications (not part of the recommended set – they are personal)
        public bool notifications = true;
        public List<string> hiddenLetters = new List<string>();
        public List<string> mutedLetters = new List<string>();
        public List<string> hiddenMessages = new List<string>();
        public List<string> mutedMessages = new List<string>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref workSpeed, "workSpeed", true);
            Scribe_Values.Look(ref colonistsOnly, "colonistsOnly", true);
            Scribe_Values.Look(ref spCraft, "spCraft", 1.5f);
            Scribe_Values.Look(ref spBuild, "spBuild", 1.5f);
            Scribe_Values.Look(ref spMine, "spMine", 1.5f);
            Scribe_Values.Look(ref spPlants, "spPlants", 1.5f);
            Scribe_Values.Look(ref spResearch, "spResearch", 1.5f);
            Scribe_Values.Look(ref spCook, "spCook", 1.5f);
            Scribe_Values.Look(ref spClean, "spClean", 1.5f);
            Scribe_Values.Look(ref spMedical, "spMedical", 1.25f);
            Scribe_Values.Look(ref spAnimals, "spAnimals", 1.5f);
            Scribe_Values.Look(ref prisoners, "prisoners", true);
            Scribe_Values.Look(ref recruitMult, "recruitMult", 2f);
            Scribe_Values.Look(ref enslaveMult, "enslaveMult", 2f);
            Scribe_Values.Look(ref unwavering, "unwavering", Unwavering.AfterDays);
            Scribe_Values.Look(ref unwaveringDays, "unwaveringDays", 15);
            Scribe_Values.Look(ref slaveRecruit, "slaveRecruit", true);
            Scribe_Values.Look(ref deconstructAll, "deconstructAll", true);
            Scribe_Values.Look(ref pipette, "pipette", true);
            Scribe_Values.Look(ref pipetteInstant, "pipetteInstant", false);
            Scribe_Values.Look(ref copyPawn, "copyPawn", true);
            Scribe_Values.Look(ref haulNow, "haulNow", true);
            Scribe_Values.Look(ref zoneTools, "zoneTools", true);
            Scribe_Values.Look(ref colonyTab, "colonyTab", true);
            Scribe_Values.Look(ref stacks, "stacks", false);
            Scribe_Values.Look(ref stackMult, "stackMult", 2f);
            Scribe_Values.Look(ref notifications, "notifications", true);
            Scribe_Collections.Look(ref hiddenLetters, "hiddenLetters", LookMode.Value);
            Scribe_Collections.Look(ref mutedLetters, "mutedLetters", LookMode.Value);
            Scribe_Collections.Look(ref hiddenMessages, "hiddenMessages", LookMode.Value);
            Scribe_Collections.Look(ref mutedMessages, "mutedMessages", LookMode.Value);
            if (hiddenLetters == null) hiddenLetters = new List<string>();
            if (mutedLetters == null) mutedLetters = new List<string>();
            if (hiddenMessages == null) hiddenMessages = new List<string>();
            if (mutedMessages == null) mutedMessages = new List<string>();
        }
    }

    public class QoLMod : Mod
    {
        public const string Version = "1.0.1";
        public static QSettings S;
        public static string StacksAtStart;   // what the stack size was set to when the game started
        private static List<string> recBullets;
        private static int page;
        private static Vector2 scroll;
        private static float viewH = 1200f;

        public QoLMod(ModContentPack content) : base(content)
        {
            S = GetSettings<QSettings>();
            MUI.Init(new QSettings(), null, "recommended", "notifications");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var h = new Harmony("bolek98.qolfasterwork");
            try { MUI.PatchWindow(h, typeof(QoLMod)); } catch { }
            try { h.PatchAll(); }
            catch (Exception e) { Log.Error("[QoL Faster Work] patching failed: " + e); }
            Log.Message("[QoL Faster Work] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "QoL – Faster Work, Prisoners & Pipette";

        public override void WriteSettings()
        {
            base.WriteSettings();
            WorkSpeed.ClearCaches();
            Startup.ApplyButton();
        }

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        private static float Mult(Listing_Standard l, string key, float v, float min, float max, string field)
        {
            float nv = MUI.Slider(l, key.Translate(v.ToString("0.##")), v, min, max, (key + "_Desc").Translate(), S, field);
            return Mathf.Round(nv * 20f) / 20f;
        }

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "QFW_Rec1".Translate(), "QFW_Rec2".Translate(), "QFW_Rec3".Translate(), "QFW_Rec4".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended && page != 3) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;

            // pages
            string[] pages = { "QFW_PageWork", "QFW_PagePrisoners", "QFW_PageTools", "QFW_PageNotify" };
            float bw = r.width / pages.Length;
            for (int i = 0; i < pages.Length; i++)
            {
                Rect b = new Rect(r.x + i * bw + 2f, y, bw - 4f, 30f);
                MUI.Fill(b, page == i ? MUI.OnBg : MUI.CardBg);
                MUI.Frame(b, page == i ? MUI.Accent : MUI.Border);
                Widgets.DrawHighlightIfMouseover(b);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = page == i ? MUI.Accent : MUI.Text1;
                Widgets.Label(b, pages[i].Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(b) && page != i) { page = i; scroll = Vector2.zero; }
            }
            y += 38f;

            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, rest.width - 18f, viewH);
            bool locked = S.recommended && page != 3;
            Widgets.BeginScrollView(rest, ref scroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(locked);
            try
            {
                l.Begin(new Rect(0f, 0f, view.width, 99999f));
                switch (page)
                {
                    case 0: PageWork(l); break;
                    case 1: PagePrisoners(l); break;
                    case 2: PageTools(l); break;
                    default: Notify.SettingsPage(l); break;
                }
                viewH = l.CurHeight + 20f;
                l.End();
            }
            finally
            {
                MUI.EndLock();
                Widgets.EndScrollView();
            }
            if (locked) MUI.Fill(rest, new Color(0.09f, 0.1f, 0.12f, 0.35f));
        }

        private static void PageWork(Listing_Standard l)
        {
            MUI.Section(l, "QFW_SecWork".Translate());
            Check(l, "QFW_WorkSpeed", ref S.workSpeed, "workSpeed");
            Check(l, "QFW_ColonistsOnly", ref S.colonistsOnly, "colonistsOnly");
            l.Gap(4f);
            S.spCraft = Mult(l, "QFW_SpCraft", S.spCraft, 0.5f, 10f, "spCraft");
            S.spBuild = Mult(l, "QFW_SpBuild", S.spBuild, 0.5f, 10f, "spBuild");
            S.spMine = Mult(l, "QFW_SpMine", S.spMine, 0.5f, 10f, "spMine");
            S.spPlants = Mult(l, "QFW_SpPlants", S.spPlants, 0.5f, 10f, "spPlants");
            S.spResearch = Mult(l, "QFW_SpResearch", S.spResearch, 0.5f, 10f, "spResearch");
            S.spCook = Mult(l, "QFW_SpCook", S.spCook, 0.5f, 10f, "spCook");
            S.spClean = Mult(l, "QFW_SpClean", S.spClean, 0.5f, 10f, "spClean");
            S.spMedical = Mult(l, "QFW_SpMedical", S.spMedical, 0.5f, 10f, "spMedical");
            S.spAnimals = Mult(l, "QFW_SpAnimals", S.spAnimals, 0.5f, 10f, "spAnimals");
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            l.Label("QFW_WorkNote".Translate(WorkSpeed.StatList()));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        private static void PagePrisoners(Listing_Standard l)
        {
            MUI.Section(l, "QFW_SecPrisoners".Translate());
            Check(l, "QFW_Prisoners", ref S.prisoners, "prisoners");
            S.recruitMult = Mult(l, "QFW_RecruitMult", S.recruitMult, 0.25f, 10f, "recruitMult");
            S.enslaveMult = Mult(l, "QFW_EnslaveMult", S.enslaveMult, 0.25f, 10f, "enslaveMult");
            MUI.RowLabel(l, "QFW_Unwavering".Translate(), "QFW_Unwavering_Desc".Translate(), S, "unwavering");
            Rect row = l.GetRect(30f);
            Unwavering[] modes = { Unwavering.Off, Unwavering.AfterDays, Unwavering.Always };
            float w = row.width / 3f;
            for (int i = 0; i < 3; i++)
            {
                Rect b = new Rect(row.x + i * w + 2f, row.y, w - 4f, 28f);
                bool on = S.unwavering == modes[i];
                MUI.Fill(b, on ? MUI.OnBg : MUI.CardBg);
                MUI.Frame(b, on ? MUI.Accent : MUI.Border);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(b, ("QFW_Unw_" + modes[i]).Translate(S.unwaveringDays));
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(b)) S.unwavering = modes[i];
            }
            l.Gap(6f);
            if (S.unwavering == Unwavering.AfterDays)
                S.unwaveringDays = Mathf.RoundToInt(MUI.Slider(l, "QFW_UnwDays".Translate(S.unwaveringDays), S.unwaveringDays, 1f, 60f, "QFW_UnwDays_Desc".Translate(), S, "unwaveringDays"));
            Check(l, "QFW_SlaveRecruit", ref S.slaveRecruit, "slaveRecruit");
        }

        private static void PageTools(Listing_Standard l)
        {
            MUI.Section(l, "QFW_SecPipette".Translate());
            Check(l, "QFW_Pipette", ref S.pipette, "pipette");
            Check(l, "QFW_PipetteInstant", ref S.pipetteInstant, "pipetteInstant");
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            l.Label("QFW_PipetteKey".Translate(Pipette.KeyLabel()));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            MUI.Section(l, "QFW_SecTools".Translate());
            Check(l, "QFW_CopyPawn", ref S.copyPawn, "copyPawn");
            Check(l, "QFW_HaulNow", ref S.haulNow, "haulNow");
            Check(l, "QFW_ZoneTools", ref S.zoneTools, "zoneTools");
            Check(l, "QFW_DeconAll", ref S.deconstructAll, "deconstructAll");
            Check(l, "QFW_ColonyTab", ref S.colonyTab, "colonyTab");
            MUI.Section(l, "QFW_SecStacks".Translate());
            Check(l, "QFW_Stacks", ref S.stacks, "stacks");
            S.stackMult = Mult(l, "QFW_StackMult", S.stackMult, 1f, 20f, "stackMult");
            string now = Startup.StackState();
            if (now != StacksAtStart)
            {
                GUI.color = MUI.Warn;
                l.Label("QFW_StacksRestart".Translate());
                GUI.color = Color.white;
            }
            if (Startup.OgreStackActive)
            {
                GUI.color = MUI.Warn;
                Text.Font = GameFont.Tiny;
                l.Label("QFW_StacksOgre".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        public static bool OgreStackActive;
        private static readonly Dictionary<ThingDef, int> origStack = new Dictionary<ThingDef, int>();

        static Startup()
        {
            try { WorkSpeed.Install(); } catch (Exception e) { Log.Error("[QoL Faster Work] work speed: " + e); }
            try
            {
                OgreStackActive = ModLister.AllInstalledMods.Any(m => m.Active && (m.Name ?? "").IndexOf("OgreStack", StringComparison.OrdinalIgnoreCase) >= 0);
                QoLMod.StacksAtStart = StackState();
                if (QoLMod.S.stacks && Mathf.Abs(QoLMod.S.stackMult - 1f) > 0.01f)
                {
                    int n = 0;
                    foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                    {
                        if (d.category != ThingCategory.Item || d.stackLimit <= 1 || d.IsCorpse || d.Minifiable || d.thingClass == typeof(MinifiedThing)) continue;
                        origStack[d] = d.stackLimit;
                        d.stackLimit = Mathf.Max(1, Mathf.RoundToInt(d.stackLimit * QoLMod.S.stackMult));
                        n++;
                    }
                    Log.Message("[QoL Faster Work] stack size ×" + QoLMod.S.stackMult.ToString("0.##") + " for " + n + " items.");
                }
            }
            catch (Exception e) { Log.Error("[QoL Faster Work] stacks: " + e); }
            ApplyButton();
        }

        public static string StackState() => QoLMod.S.stacks ? QoLMod.S.stackMult.ToString("0.##") : "off";

        public static void ApplyButton()
        {
            try
            {
                MainButtonDef b = DefDatabase<MainButtonDef>.GetNamedSilentFail("QFW_Colony");
                if (b != null) b.buttonVisible = QoLMod.S.colonyTab;
            }
            catch { }
        }
    }

    // Small helpers shared by the tools.
    public static class Q
    {
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(d.Length);
            foreach (char c in d)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (c == '-' || c == '_' || c == '–' || c == '\'') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static void Msg(string text, LookTargets t = null, bool bad = false) =>
            Messages.Message(text, t ?? LookTargets.Invalid, bad ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.NeutralEvent, false);
    }
}
