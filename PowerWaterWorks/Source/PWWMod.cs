using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PowerWaterWorks
{
    // ---------------------------------------------------------------------------
    // Power & Water Works 0.1.0 – new generators (hydro, biogas, deep geothermal, incinerator,
    // diesel with auto-start, fission reactor with cooling towers, fusion reactor), storage
    // (big battery, flywheel), a circuit breaker, a grid overview, electric factories and –
    // with Dubs Bad Hygiene – water buildings (Defs in the DBH folder).
    // ---------------------------------------------------------------------------
    public class PWWSettings : ModSettings
    {
        public bool meltdown;                 // false = the reactor shuts itself down at 100 % heat
        public float autoLow = 0.2f;          // diesel auto-start: on below this stored energy
        public float autoHigh = 0.8f;         // … off above this
        public bool hydroWinter = true;       // hydro plants give 40 % when the river freezes

        public override void ExposeData()
        {
            Scribe_Values.Look(ref meltdown, "meltdown", false);
            Scribe_Values.Look(ref autoLow, "autoLow", 0.2f);
            Scribe_Values.Look(ref autoHigh, "autoHigh", 0.8f);
            Scribe_Values.Look(ref hydroWinter, "hydroWinter", true);
        }
    }

    public class PWWMod : Mod
    {
        public const string Version = "0.1.0";
        public static PWWSettings S;

        public PWWMod(ModContentPack content) : base(content)
        {
            S = GetSettings<PWWSettings>();
            try
            {
                var h = new Harmony("bolek98.powerwaterworks");
                h.PatchAll();
                Log.Message("[Power & Water Works] v" + Version + " loaded.");
            }
            catch (Exception e) { Log.Error("[Power & Water Works] patching failed: " + e); }
        }

        public override string SettingsCategory() => "Power & Water Works";

        public override void DoSettingsWindowContents(Rect r)
        {
            var l = new Listing_Standard();
            l.Begin(r);
            l.CheckboxLabeled("PWW_SetMeltdown".Translate(), ref S.meltdown, "PWW_SetMeltdownTip".Translate());
            l.CheckboxLabeled("PWW_SetHydroWinter".Translate(), ref S.hydroWinter, "PWW_SetHydroWinterTip".Translate());
            l.Gap();
            l.Label("PWW_SetAutoLow".Translate((S.autoLow * 100f).ToString("0")));
            S.autoLow = Mathf.Round(l.Slider(S.autoLow, 0f, 0.9f) * 20f) / 20f;
            l.Label("PWW_SetAutoHigh".Translate((S.autoHigh * 100f).ToString("0")));
            S.autoHigh = Mathf.Round(l.Slider(S.autoHigh, 0.1f, 1f) * 20f) / 20f;
            if (S.autoHigh <= S.autoLow) S.autoHigh = Mathf.Min(1f, S.autoLow + 0.05f);
            l.End();
        }
    }

    [StaticConstructorOnStartup]
    public static class PWWStartup
    {
        static PWWStartup()
        {
            // the hydro plant stands partly on water: its own place worker checks the ground
            ThingDef hydro = DefDatabase<ThingDef>.GetNamedSilentFail("PWW_HydroPlant");
            if (hydro != null) hydro.terrainAffordanceNeeded = null;
        }
    }

    // ---------------- circuit breaker: a short circuit trips it instead of exploding ----------------
    [HarmonyPatch(typeof(ShortCircuitUtility), nameof(ShortCircuitUtility.DoShortCircuit))]
    public static class Patch_ShortCircuit
    {
        public static bool Prefix(Building culprit)
        {
            try
            {
                PowerNet net = culprit?.PowerComp?.PowerNet;
                if (net == null) return true;
                foreach (CompPower t in net.transmitters)
                {
                    if (t?.parent?.def?.defName != "PWW_CircuitBreaker") continue;
                    CompFlickable f = t.parent.TryGetComp<CompFlickable>();
                    if (f == null || !f.SwitchIsOn) continue;
                    f.SwitchIsOn = false;
                    Find.LetterStack.ReceiveLetter("PWW_BreakerTrippedLabel".Translate(), "PWW_BreakerTripped".Translate(), LetterDefOf.NeutralEvent, new TargetInfo(t.parent.Position, t.parent.Map));
                    return false;
                }
            }
            catch (Exception e) { Log.WarningOnce("[Power & Water Works] breaker: " + e.Message, 0x7b1e01); }
            return true;
        }
    }

    // ---------------- textile mill: worn apparel becomes cloth ----------------
    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    public static class Patch_TextileRecycle
    {
        public static IEnumerable<Thing> Postfix(IEnumerable<Thing> values, RecipeDef recipeDef, List<Thing> ingredients)
        {
            foreach (Thing t in values) yield return t;
            if (recipeDef?.defName != "PWW_RecycleApparel" || ingredients == null) yield break;
            int n = 0;
            foreach (Thing ing in ingredients)
            {
                if (ing?.def == null || !ing.def.IsApparel) continue;
                int stuff = ing.def.MadeFromStuff ? ing.def.CostStuffCount : 0;
                int cloth = ing.def.costList?.Where(c => c.thingDef == ThingDefOf.Cloth).Sum(c => c.count) ?? 0;
                n += Mathf.Max(1, Mathf.RoundToInt((stuff + cloth) * 0.3f * ing.stackCount));
            }
            if (n <= 0) yield break;
            while (n > 0)
            {
                Thing c = ThingMaker.MakeThing(ThingDefOf.Cloth);
                c.stackCount = Mathf.Min(n, ThingDefOf.Cloth.stackLimit);
                n -= c.stackCount;
                yield return c;
            }
        }
    }
}
