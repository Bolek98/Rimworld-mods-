using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace UnofficialPatch
{
    // Performance fixes. Each one does what the vanilla code does, only without work whose
    // result is thrown away. Findings: Dreadarm (profiling of 1.7.4982) and mrbeverage (raid
    // steal check), RimWorld official development Discord, #bug-reports, October 2026.
    //
    // Every fix lives in its own class, so a renamed field in a future build can only break that
    // one fix. Fixes that replace a whole vanilla method step aside (run vanilla) as soon as any
    // other mod patches that method – checked once the game is running, when all mods have patched.
    public static class Perf
    {
        // true when a mod other than us patches the method
        public static bool OthersPatch(Type t, string method)
        {
            MethodInfo m = AccessTools.DeclaredMethod(t, method);
            if (m == null)
                return false;
            Patches p = Harmony.GetPatchInfo(m);
            if (p == null)
                return false;
            foreach (string owner in p.Owners)
                if (owner != "bolek98.unofficialpatch")
                    return true;
            return false;
        }

        // 0 = not decided yet, 1 = our fix may run, 2 = another mod patches the same code
        public static bool Decide(ref int state, Func<bool> othersPatch)
        {
            if (state == 0)
            {
                if (Current.ProgramState != ProgramState.Playing)
                    return false;
                state = othersPatch() ? 2 : 1;
            }
            return state == 1;
        }

        public static void PatchWind(Harmony h) => Wind.Patch(h);
        public static void PatchFreeze(Harmony h) => Freeze.Patch(h);
        public static void PatchGas(Harmony h) => Gas.Patch(h);
        public static void PatchSealed(Harmony h) => Sealed.Patch(h);
        public static void PatchWorld(Harmony h) => World.Patch(h);
        public static void PatchHealth(Harmony h) => Health.Patch(h);
        public static void PatchSteal(Harmony h) => Steal.Patch(h);
    }

    // ------------------------------------------------------------------
    // 1) Plant wind sway. WindManagerTick pushed the sway value into every plant material on EVERY
    //    simulation tick (a locked GPU uniform write each), but it is only seen once per drawn frame.
    //    The tick now only advances the value; it is pushed once per frame at the start of
    //    Map.MapUpdate (after the ticks of that frame, before drawing), and only when it changed.
    // ------------------------------------------------------------------
    public static class Wind
    {
        private static readonly AccessTools.FieldRef<WindManager, float> swayHead = AccessTools.FieldRefAccess<WindManager, float>("plantSwayHead");
        private static List<Material> plantMaterials;
        private static float pushedHead = float.NaN;
        private static int pushedCount = -1;
        private static int pushedMap = -1;

        public static void Patch(Harmony h)
        {
            plantMaterials = (List<Material>)AccessTools.Field(typeof(WindManager), "plantMaterials").GetValue(null);
            h.Patch(AccessTools.Method(typeof(WindManager), nameof(WindManager.WindManagerTick)),
                transpiler: new HarmonyMethod(typeof(Wind), nameof(Transpiler)));
            h.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)),
                prefix: new HarmonyMethod(typeof(Wind), nameof(MapUpdatePrefix)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
        {
            MethodInfo setFloat = AccessTools.Method(typeof(Material), nameof(Material.SetFloat), new[] { typeof(int), typeof(float) });
            int n = 0;
            foreach (CodeInstruction c in codes)
            {
                if (c.Calls(setFloat))
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Wind), nameof(SwaySet))).MoveLabelsFrom(c).MoveBlocksFrom(c);
                    continue;
                }
                yield return c;
            }
            if (n != 1)
                throw new Exception("expected one SetFloat call in WindManagerTick, found " + n);
        }

        // replaces Material.SetFloat inside WindManagerTick
        public static void SwaySet(Material m, int id, float value)
        {
            if (!UnofficialPatchMod.OnWind)
                m.SetFloat(id, value);
        }

        public static void MapUpdatePrefix(Map __instance)
        {
            try
            {
                if (!UnofficialPatchMod.OnWind || __instance != Find.CurrentMap || __instance.windManager == null || plantMaterials == null)
                    return;
                float v = swayHead(__instance.windManager);
                if (v == pushedHead && plantMaterials.Count == pushedCount && __instance.uniqueID == pushedMap)
                    return;
                for (int k = 0; k < plantMaterials.Count; k++)
                    plantMaterials[k].SetFloat(ShaderPropertyIDs.SwayHead, v);
                pushedHead = v;
                pushedCount = plantMaterials.Count;
                pushedMap = __instance.uniqueID;
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // 2) Freezing and melting (Odyssey). 38 random cells a tick looked up temperature AND room
    //    (twice) before checking whether the cell is water at all. Same rules, same random rolls,
    //    the cheap checks first and the room only when something will happen.
    // ------------------------------------------------------------------
    public static class Freeze
    {
        private static readonly AccessTools.FieldRef<FreezeManager, Map> fmMap = AccessTools.FieldRefAccess<FreezeManager, Map>("map");
        private static Action<FreezeManager, IntVec3> doWaterFreezing;
        private static Func<FreezeManager, IntVec3, bool> canEverMelt, canMelt;
        private static readonly List<IntVec3> tmpMelt = new List<IntVec3>();
        private static int safe;

        public static void Patch(Harmony h)
        {
            doWaterFreezing = AccessTools.MethodDelegate<Action<FreezeManager, IntVec3>>(AccessTools.Method(typeof(FreezeManager), "DoWaterFreezing"));
            canEverMelt = AccessTools.MethodDelegate<Func<FreezeManager, IntVec3, bool>>(AccessTools.Method(typeof(FreezeManager), "CanEverMelt"));
            canMelt = AccessTools.MethodDelegate<Func<FreezeManager, IntVec3, bool>>(AccessTools.Method(typeof(FreezeManager), "CanMelt"));
            h.Patch(AccessTools.Method(typeof(FreezeManager), nameof(FreezeManager.DoCellSteadyEffects)),
                prefix: new HarmonyMethod(typeof(Freeze), nameof(Prefix)));
        }

        public static bool Prefix(FreezeManager __instance, IntVec3 c)
        {
            if (!UnofficialPatchMod.OnFreeze || !Perf.Decide(ref safe, () =>
                    Perf.OthersPatch(typeof(FreezeManager), nameof(FreezeManager.DoCellSteadyEffects)) || Perf.OthersPatch(typeof(FreezeManager), "DoIceMelting")))
                return true;
            if (!ModsConfig.OdysseyActive)
                return false;
            Map map = fmMap(__instance);
            // DoWaterFreezing returns before any random roll unless the cell can freeze
            if (c.GetTerrain(map).canFreeze)
                doWaterFreezing(__instance, c);
            // DoIceMelting: same checks, same random roll
            float temperature = c.GetTemperature(map);
            if (temperature < 2f || !Rand.Chance(0.001f * ((temperature + 5f) / 5f)))
                return false;
            Room room = c.GetRoom(map);
            tmpMelt.Clear();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(c, 2.9f, useCenter: true))
                if (cell.InBounds(map) && cell.GetRoom(map) == room && canEverMelt(__instance, cell) && canMelt(__instance, cell))
                    tmpMelt.Add(cell);
            for (int i = 0; i < tmpMelt.Count; i++)
                map.terrainGrid.RemoveTempTerrain(tmpMelt[i]);
            tmpMelt.Clear();
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 3) Gas. Once any gas ever appeared (one rotting corpse is enough), ~2 900 cells a tick were
    //    visited although almost all are empty. Empty cells are skipped before the call – the
    //    vanilla functions return at once for them anyway (no gas / under 17).
    // ------------------------------------------------------------------
    public static class Gas
    {
        private static readonly AccessTools.FieldRef<GasGrid, uint[]> gasDensity = AccessTools.FieldRefAccess<GasGrid, uint[]>("gasDensity");
        private static readonly AccessTools.FieldRef<GasGrid, Map> gasMap = AccessTools.FieldRefAccess<GasGrid, Map>("map");
        private static readonly AccessTools.FieldRef<GasGrid, int> cycleDiffusion = AccessTools.FieldRefAccess<GasGrid, int>("cycleIndexDiffusion");
        private static readonly AccessTools.FieldRef<GasGrid, int> cycleDissipation = AccessTools.FieldRefAccess<GasGrid, int>("cycleIndexDissipation");
        private static readonly AccessTools.FieldRef<GasGrid, List<IntVec3>> gasCells = AccessTools.FieldRefAccess<GasGrid, List<IntVec3>>("cellsInRandomOrder");
        private static Action<GasGrid, int> tryDissipate;
        private static Action<GasGrid, IntVec3> tryDiffuse;
        private static int safe;

        public static void Patch(Harmony h)
        {
            tryDissipate = AccessTools.MethodDelegate<Action<GasGrid, int>>(AccessTools.Method(typeof(GasGrid), "TryDissipateGases"));
            tryDiffuse = AccessTools.MethodDelegate<Action<GasGrid, IntVec3>>(AccessTools.Method(typeof(GasGrid), "TryDiffuseGases"));
            h.Patch(AccessTools.Method(typeof(GasGrid), nameof(GasGrid.Tick)),
                prefix: new HarmonyMethod(typeof(Gas), nameof(Prefix)));
        }

        public static bool Prefix(GasGrid __instance)
        {
            if (!UnofficialPatchMod.OnGas || !Perf.Decide(ref safe, () =>
                    Perf.OthersPatch(typeof(GasGrid), nameof(GasGrid.Tick)) || Perf.OthersPatch(typeof(GasGrid), "TryDissipateGases") || Perf.OthersPatch(typeof(GasGrid), "TryDiffuseGases")))
                return true;
            if (!__instance.CalculateGasEffects)
                return false;
            Map map = gasMap(__instance);
            uint[] dens = gasDensity(__instance);
            int area = map.Area;
            int sizeX = map.Size.x;
            List<IntVec3> cells = map.cellsInRandomOrder.GetAll();
            gasCells(__instance) = cells;
            ref int dis = ref cycleDissipation(__instance);
            int num = Mathf.CeilToInt(area * (1f / 64f));
            for (int i = 0; i < num; i++)
            {
                if (dis >= area)
                    dis = 0;
                int index = CellIndicesUtility.CellToIndex(cells[dis], sizeX);
                if (dens[index] != 0)
                    tryDissipate(__instance, index);
                dis++;
            }
            ref int dif = ref cycleDiffusion(__instance);
            num = Mathf.CeilToInt(area * (1f / 32f));
            for (int j = 0; j < num; j++)
            {
                if (dif >= area)
                    dif = 0;
                IntVec3 cell = cells[dif];
                // TryDiffuseGases needs toxic + rot + deadlife >= 17 (bits 8..31); smoke never diffuses
                if ((dens[CellIndicesUtility.CellToIndex(cell, sizeX)] >> 8) != 0)
                    tryDiffuse(__instance, cell);
                dif++;
            }
            if (map.IsHashIntervalTick(600))
                __instance.RecalculateEverHadGas();
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 4) GetComp<T>() asked "is this type sealed?" through a global lock thousands of times a tick.
    //    Same answer from a lock-free cache.
    // ------------------------------------------------------------------
    public static class Sealed
    {
        private static readonly ConcurrentDictionary<Type, bool> cache = new ConcurrentDictionary<Type, bool>();

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.IsSealedWithCache)),
                prefix: new HarmonyMethod(typeof(Sealed), nameof(Prefix)));
        }

        public static bool Prefix(Type type, ref bool __result)
        {
            if (!UnofficialPatchMod.OnSealed || type == null)
                return true;
            if (!cache.TryGetValue(type, out bool v))
            {
                v = type.IsSealed;
                cache[type] = v;
            }
            __result = v;
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 5) Settlements on the world map. Every tick each of the 240+ settlements without a map
    //    collected its "thing holders" just to reach the trader stock – which never ticks
    //    (dontTickContents). Plain settlements without a map now stop after their own tick.
    //    Steps aside when another mod changes any part of that path.
    // ------------------------------------------------------------------
    public static class World
    {
        private static readonly AccessTools.FieldRef<Settlement_TraderTracker, ThingOwner<Thing>> traderStock =
            AccessTools.FieldRefAccess<Settlement_TraderTracker, ThingOwner<Thing>>("stock");
        private static int safe;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(WorldObject), nameof(WorldObject.DoTick)),
                transpiler: new HarmonyMethod(typeof(World), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes, ILGenerator gen)
        {
            FieldInfo tmpHolders = AccessTools.Field(typeof(WorldObject), "tmpHolders");
            var list = new List<CodeInstruction>(codes);
            int at = list.FindIndex(c => c.LoadsField(tmpHolders));
            if (at < 1 || list[at - 1].opcode != OpCodes.Ldarg_0)
                throw new Exception("WorldObject.DoTick: tmpHolders not found");
            at--;
            Label cont = gen.DefineLabel();
            var first = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(list[at]);
            list[at].labels.Add(cont);
            list.InsertRange(at, new[]
            {
                first,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(World), nameof(SkipHolders))),
                new CodeInstruction(OpCodes.Brfalse, cont),
                new CodeInstruction(OpCodes.Ret)
            });
            return list;
        }

        public static bool SkipHolders(WorldObject wo)
        {
            if (!UnofficialPatchMod.OnWorld || wo.GetType() != typeof(Settlement))
                return false;
            if (!Perf.Decide(ref safe, () =>
                    Perf.OthersPatch(typeof(Settlement), "GetChildHolders") || Perf.OthersPatch(typeof(MapParent), "GetChildHolders")
                    || Perf.OthersPatch(typeof(MapParent), "GetDirectlyHeldThings") || Perf.OthersPatch(typeof(Settlement_TraderTracker), "GetDirectlyHeldThings")
                    || Perf.OthersPatch(typeof(ThingOwner), nameof(ThingOwner.DoTick))))
                return false;
            var s = (Settlement)wo;
            if (s.HasMap)
                return false;
            if (s.trader != null)
            {
                ThingOwner<Thing> stock = traderStock(s.trader);
                if (stock != null && !stock.dontTickContents)
                    return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------
    // 6) Health tick of pawns without any hediff: the empty list was still copied every tick.
    // ------------------------------------------------------------------
    public static class Health
    {
        private static int safe;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)),
                prefix: new HarmonyMethod(typeof(Health), nameof(Prefix)));
        }

        public static bool Prefix(Pawn_HealthTracker __instance)
        {
            if (!UnofficialPatchMod.OnHealth)
                return true;
            List<Hediff> l = __instance.hediffSet?.hediffs;
            if (l == null || l.Count != 0)
                return true;
            return !Perf.Decide(ref safe, () => Perf.OthersPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)));
        }
    }

    // ------------------------------------------------------------------
    // 7) Raid steal check (mrbeverage). Every 2 s each raider priced every tree and rock chunk
    //    around it, and pricing an item with no cost list searched ALL recipes of the game (2 000+
    //    with many mods). The answer is remembered per item kind while playing. It is forgotten when
    //    recipes change (count, difficulty-dependent ingredients) or the difficulty changes; items
    //    whose cost depends on the difficulty are never remembered.
    // ------------------------------------------------------------------
    public static class Steal
    {
        private static readonly ConcurrentDictionary<BuildableDef, RecipeDef> cache = new ConcurrentDictionary<BuildableDef, RecipeDef>();
        private static int recipeCount = -1;
        private static object difficulty;
        private static int safe;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculableRecipe)),
                prefix: new HarmonyMethod(typeof(Steal), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(Steal), nameof(Postfix)) { priority = Priority.Last });
            MethodInfo reset = AccessTools.Method(typeof(RecipeDefGenerator), nameof(RecipeDefGenerator.ResetRecipeIngredientsForDifficulty));
            if (reset != null)
                h.Patch(reset, postfix: new HarmonyMethod(typeof(Steal), nameof(Clear)));
        }

        public static void Clear() => cache.Clear();

        public static bool Prefix(BuildableDef def, ref RecipeDef __result, out bool __state)
        {
            __state = false;
            if (!UnofficialPatchMod.OnSteal || def == null || def.costListForDifficulty != null)
                return true;
            if (!Perf.Decide(ref safe, () => Perf.OthersPatch(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculableRecipe))))
                return true;
            int count = DefDatabase<RecipeDef>.DefCount;
            object diff = Find.Storyteller?.difficulty;
            if (count != recipeCount || !ReferenceEquals(diff, difficulty))
            {
                cache.Clear();
                recipeCount = count;
                difficulty = diff;
            }
            if (cache.TryGetValue(def, out RecipeDef r))
            {
                __result = r;
                return false;
            }
            __state = true;   // let the game compute it, remember it in the postfix
            return true;
        }

        public static void Postfix(BuildableDef def, RecipeDef __result, bool __state)
        {
            if (__state && def != null)
                cache[def] = __result;
        }
    }
}
