using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PensForAll
{
    // ---------------------------------------------------------------------------
    // Pens For All Animals 0.1.0. In the game only "roaming" farm animals can be put into an
    // animal pen. With this mod every animal shows up in the pen's animal list. An animal kind
    // that is ticked in at least one pen becomes a pen animal for as long as it stays ticked:
    // colonists lead it into the pen with a rope, fences hold it, it eats the grass there.
    // Untick it in every pen and it is a normal animal again (areas, following its master …).
    // ---------------------------------------------------------------------------
    public class PenSettings : ModSettings
    {
        public bool recommended = true;
        public bool enabled = true;          // every animal in the pen list
        public bool keepPetsOut = false;     // animals with a master or bonded stay normal animals
        public float roamDays = 6f;          // how often an animal outside its pen wanders off (game uses 2–12)

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref keepPetsOut, "keepPetsOut", false);
            Scribe_Values.Look(ref roamDays, "roamDays", 6f);
        }
    }

    public class PensForAllMod : Mod
    {
        public const string Version = "0.1.0";
        public static PenSettings S;
        private static List<string> recBullets;

        public PensForAllMod(ModContentPack content) : base(content)
        {
            S = GetSettings<PenSettings>();
            MUI.Init(new PenSettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            try { MUI.PatchWindow(new Harmony("bolek98.pensforall"), typeof(PensForAllMod)); } catch { }
            Log.Message("[Pens For All] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "PFA_ModName".Translate();

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "PFA_Rec1".Translate(), "PFA_Rec2".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(rest);
                MUI.Check(l, "PFA_Enabled".Translate(), "PFA_Enabled_Desc".Translate(), ref S.enabled, S, "enabled");
                MUI.Check(l, "PFA_Pets".Translate(), "PFA_Pets_Desc".Translate(), ref S.keepPetsOut, S, "keepPetsOut");
                S.roamDays = Mathf.Round(MUI.Slider(l, "PFA_Roam".Translate(S.roamDays.ToString("0")), S.roamDays, 2f, 30f, "PFA_Roam_Desc".Translate(), S, "roamDays"));
                l.End();
            }
            finally { MUI.EndLock(rest); }
            Pens.Dirty();
        }
    }

    public static class Pens
    {
        // animal kinds that roam in the base game (never changed by this mod)
        private static readonly Dictionary<ThingDef, float?> original = new Dictionary<ThingDef, float?>();
        private static readonly HashSet<ThingDef> converted = new HashSet<ThingDef>();
        private static List<ThingDef> animals;
        private static bool dirty = true;

        public static void Dirty() { dirty = true; }

        private static List<ThingDef> Animals
        {
            get
            {
                if (animals == null)
                {
                    animals = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(d => d.race != null && d.race.Animal && !d.race.Humanlike && d.IsWithinCategory(ThingCategoryDefOf.Animals))
                        .ToList();
                    foreach (ThingDef d in animals)
                        if (!original.ContainsKey(d)) original[d] = d.race.roamMtbDays;
                }
                return animals;
            }
        }

        // the pen's animal list: every animal (the game builds it from roaming kinds only)
        public static void ExtendPenList()
        {
            try
            {
                if (PensForAllMod.S == null || !PensForAllMod.S.enabled) return;
                ThingFilter fixedFilter = AnimalPenUtility.GetFixedAnimalFilter();
                int n = 0;
                foreach (ThingDef d in Animals)
                    if (!fixedFilter.Allows(d)) { fixedFilter.SetAllow(d, true); n++; }
                if (n > 0) Log.Message("[Pens For All] " + n + " more animal kinds can be put into pens.");
            }
            catch (Exception e) { Log.Warning("[Pens For All] could not extend the pen list: " + e.Message); }
        }

        // every kind back to the game's values (a new or loaded game starts clean)
        public static void RestoreAll()
        {
            foreach (ThingDef d in converted)
                if (original.TryGetValue(d, out float? v)) d.race.roamMtbDays = v;
            converted.Clear();
            dirty = true;
        }

        // kinds ticked in a pen become pen animals; unticked ones go back
        public static void Update()
        {
            var wanted = new HashSet<ThingDef>();
            if (PensForAllMod.S != null && PensForAllMod.S.enabled)
            {
                foreach (Map map in Find.Maps)
                {
                    if (!map.IsPlayerHome) continue;
                    foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    {
                        CompAnimalPenMarker pen = b.TryGetComp<CompAnimalPenMarker>();
                        if (pen?.AnimalFilter == null) continue;
                        foreach (ThingDef d in pen.AnimalFilter.AllowedThingDefs)
                            if (d.race != null && original.TryGetValue(d, out float? o) && !o.HasValue) wanted.Add(d);
                    }
                }
            }
            float days = Mathf.Clamp(PensForAllMod.S?.roamDays ?? 6f, 1f, 60f);
            foreach (ThingDef d in converted.ToList())
                if (!wanted.Contains(d)) { d.race.roamMtbDays = original[d]; converted.Remove(d); }
            foreach (ThingDef d in wanted)
            {
                if (!converted.Contains(d)) converted.Add(d);
                d.race.roamMtbDays = days;
            }
            dirty = false;
        }

        public static bool IsConverted(ThingDef d) => converted.Contains(d);

        public static void Tick()
        {
            if (dirty || Find.TickManager.TicksGame % 250 == 0)
            {
                try { if (Animals.Count > 0) Update(); }
                catch (Exception e) { Log.WarningOnce("[Pens For All] update failed: " + e.Message, 0x7E51A1); }
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class PensStartup
    {
        static PensStartup()
        {
            Pens.ExtendPenList();
            try
            {
                var h = new Harmony("bolek98.pensforall");
                // 0.1.0: pets (with a master or a bond) can be kept out of pen rules – they keep areas and follow
                h.Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.RoamMtbDays)), postfix: new HarmonyMethod(typeof(PensStartup), nameof(RoamPostfix)));
            }
            catch (Exception e) { Log.Warning("[Pens For All] patch failed: " + e.Message); }
        }

        public static void RoamPostfix(Pawn __instance, ref float? __result)
        {
            if (!__result.HasValue || PensForAllMod.S == null || !PensForAllMod.S.keepPetsOut) return;
            try
            {
                if (!Pens.IsConverted(__instance.def)) return;
                if (__instance.playerSettings?.Master != null || __instance.relations?.GetFirstDirectRelationPawn(PawnRelationDefOf.Bond) != null)
                    __result = null;
            }
            catch { }
        }
    }

    public class PensGameComponent : GameComponent
    {
        public PensGameComponent(Game game)
        {
            Pens.RestoreAll();      // the kinds a previous game turned into pen animals start clean
        }

        public override void FinalizeInit()
        {
            Pens.ExtendPenList();
            Pens.Dirty();
        }

        public override void GameComponentTick() => Pens.Tick();
    }
}
