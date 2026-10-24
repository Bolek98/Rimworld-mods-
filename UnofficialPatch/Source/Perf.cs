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
    // Performance fixes. Each one does exactly what the vanilla code does, only without work whose
    // result is thrown away. Findings: Dreadarm (profiling of 1.7.4982) and mrbeverage (raid
    // steal check), RimWorld official development Discord, #bug-reports, October 2026.
    public static class Perf
    {
        // ------------------------------------------------------------------
        // 1) Plant wind sway. WindManagerTick pushed the sway value into every plant material
        //    on EVERY simulation tick (a locked GPU uniform write each), but it is only seen once
        //    per drawn frame. At speed 3–4 that is many writes per frame for nothing.
        //    Now the tick only advances the value; the value is pushed once per frame, and only
        //    when it changed.
        // ------------------------------------------------------------------
        private static readonly AccessTools.FieldRef<WindManager, float> swayHead = AccessTools.FieldRefAccess<WindManager, float>("plantSwayHead");
        private static List<Material> plantMaterials;
        private static float pushedHead = float.NaN;
        private static int pushedCount = -1;
        private static Map pushedMap;

        public static void PatchWind(Harmony h)
        {
            plantMaterials = (List<Material>)AccessTools.Field(typeof(WindManager), "plantMaterials").GetValue(null);
            h.Patch(AccessTools.Method(typeof(WindManager), nameof(WindManager.WindManagerTick)),
                transpiler: new HarmonyMethod(typeof(Perf), nameof(WindTranspiler)));
            h.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)),
                postfix: new HarmonyMethod(typeof(Perf), nameof(MapUpdatePostfix)));
        }

        public static IEnumerable<CodeInstruction> WindTranspiler(IEnumerable<CodeInstruction> codes)
        {
            MethodInfo setFloat = AccessTools.Method(typeof(Material), nameof(Material.SetFloat), new[] { typeof(int), typeof(float) });
            int n = 0;
            foreach (CodeInstruction c in codes)
            {
                if (c.Calls(setFloat))
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Perf), nameof(SwaySet))).MoveLabelsFrom(c).MoveBlocksFrom(c);
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

        public static void MapUpdatePostfix(Map __instance)
        {
            if (!UnofficialPatchMod.OnWind || __instance != Find.CurrentMap || __instance.windManager == null || plantMaterials == null)
                return;
            float v = swayHead(__instance.windManager);
            if (v == pushedHead && plantMaterials.Count == pushedCount && __instance == pushedMap)
                return;
            for (int k = 0; k < plantMaterials.Count; k++)
                plantMaterials[k].SetFloat(ShaderPropertyIDs.SwayHead, v);
            pushedHead = v;
            pushedCount = plantMaterials.Count;
            pushedMap = __instance;
        }

        // ------------------------------------------------------------------
        // 2) Freezing and melting (Odyssey). 38 random cells a tick looked up temperature AND room
        //    twice before checking whether the cell is water at all. Same rules, same random
        //    rolls, but the cheap checks first and the room only when something will happen.
        // ------------------------------------------------------------------
        private static readonly AccessTools.FieldRef<FreezeManager, Map> fmMap = AccessTools.FieldRefAccess<FreezeManager, Map>("map");
        private static Action<FreezeManager, IntVec3> doWaterFreezing;
        private static Func<FreezeManager, IntVec3, bool> canEverMelt, canMelt;
        private static readonly List<IntVec3> tmpMelt = new List<IntVec3>();

        public static void PatchFreeze(Harmony h)
        {
            doWaterFreezing = AccessTools.MethodDelegate<Action<FreezeManager, IntVec3>>(AccessTools.Method(typeof(FreezeManager), "DoWaterFreezing"));
            canEverMelt = AccessTools.MethodDelegate<Func<FreezeManager, IntVec3, bool>>(AccessTools.Method(typeof(FreezeManager), "CanEverMelt"));
            canMelt = AccessTools.MethodDelegate<Func<FreezeManager, IntVec3, bool>>(AccessTools.Method(typeof(FreezeManager), "CanMelt"));
            h.Patch(AccessTools.Method(typeof(FreezeManager), nameof(FreezeManager.DoCellSteadyEffects)),
                prefix: new HarmonyMethod(typeof(Perf), nameof(FreezePrefix)));
        }

        public static bool FreezePrefix(FreezeManager __instance, IntVec3 c)
        {
            if (!UnofficialPatchMod.OnFreeze)
                return true;
            if (!ModsConfig.OdysseyActive)
                return false;
            Map map = fmMap(__instance);
            // DoWaterFreezing: returns before any random roll unless the cell can freeze
            if (c.GetTerrain(map).canFreeze)
                doWaterFreezing(__instance, c);
            // DoIceMelting, same order of checks and the same random roll
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

        // ------------------------------------------------------------------
        // 3) Gas. Once any gas ever appeared (one rotting corpse is enough), ~2 900 cells a tick
        //    were visited although almost all are empty. Empty cells are now skipped before the
        //    call – the vanilla functions return at once for them anyway (no gas / under 17).
        // ------------------------------------------------------------------
        private static readonly AccessTools.FieldRef<GasGrid, uint[]> gasDensity = AccessTools.FieldRefAccess<GasGrid, uint[]>("gasDensity");
        private static readonly AccessTools.FieldRef<GasGrid, Map> gasMap = AccessTools.FieldRefAccess<GasGrid, Map>("map");
        private static readonly AccessTools.FieldRef<GasGrid, int> cycleDiffusion = AccessTools.FieldRefAccess<GasGrid, int>("cycleIndexDiffusion");
        private static readonly AccessTools.FieldRef<GasGrid, int> cycleDissipation = AccessTools.FieldRefAccess<GasGrid, int>("cycleIndexDissipation");
        private static readonly AccessTools.FieldRef<GasGrid, List<IntVec3>> gasCells = AccessTools.FieldRefAccess<GasGrid, List<IntVec3>>("cellsInRandomOrder");
        private static Action<GasGrid, int> tryDissipate;
        private static Action<GasGrid, IntVec3> tryDiffuse;

        public static void PatchGas(Harmony h)
        {
            tryDissipate = AccessTools.MethodDelegate<Action<GasGrid, int>>(AccessTools.Method(typeof(GasGrid), "TryDissipateGases"));
            tryDiffuse = AccessTools.MethodDelegate<Action<GasGrid, IntVec3>>(AccessTools.Method(typeof(GasGrid), "TryDiffuseGases"));
            h.Patch(AccessTools.Method(typeof(GasGrid), nameof(GasGrid.Tick)),
                prefix: new HarmonyMethod(typeof(Perf), nameof(GasPrefix)));
        }

        public static bool GasPrefix(GasGrid __instance)
        {
            if (!UnofficialPatchMod.OnGas)
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

        // ------------------------------------------------------------------
        // 4) GetComp<T>() asked "is this type sealed?" through a global lock thousands of times a
        //    tick. Same answer from a lock-free cache.
        // ------------------------------------------------------------------
        private static readonly ConcurrentDictionary<Type, bool> sealedCache = new ConcurrentDictionary<Type, bool>();

        public static void PatchSealed(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.IsSealedWithCache)),
                prefix: new HarmonyMethod(typeof(Perf), nameof(SealedPrefix)));
        }

        public static bool SealedPrefix(Type type, ref bool __result)
        {
            if (!UnofficialPatchMod.OnSealed || type == null)
                return true;
            if (!sealedCache.TryGetValue(type, out bool v))
            {
                v = type.IsSealed;
                sealedCache[type] = v;
            }
            __result = v;
            return false;
        }

        // ------------------------------------------------------------------
        // 5) Settlements on the world map. Every tick each of the 240+ settlements without a map
        //    collected its "thing holders" just to find the trader stock – which never ticks
        //    (dontTickContents). Plain settlements without a map now stop after their own tick.
        //    Only when no other mod changes what a settlement holds.
        // ------------------------------------------------------------------
        private static readonly AccessTools.FieldRef<Settlement_TraderTracker, ThingOwner<Thing>> traderStock =
            AccessTools.FieldRefAccess<Settlement_TraderTracker, ThingOwner<Thing>>("stock");
        private static int worldSafe;   // 0 = not checked yet, 1 = safe, 2 = another mod changes holders

        public static void PatchWorld(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(WorldObject), nameof(WorldObject.DoTick)),
                transpiler: new HarmonyMethod(typeof(Perf), nameof(WorldTranspiler)));
        }

        public static IEnumerable<CodeInstruction> WorldTranspiler(IEnumerable<CodeInstruction> codes, ILGenerator gen)
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
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Perf), nameof(SkipHolders))),
                new CodeInstruction(OpCodes.Brfalse, cont),
                new CodeInstruction(OpCodes.Ret)
            });
            return list;
        }

        public static bool SkipHolders(WorldObject wo)
        {
            if (!UnofficialPatchMod.OnWorld || wo.GetType() != typeof(Settlement))
                return false;
            if (worldSafe == 0)
                worldSafe = OthersPatch(typeof(Settlement), "GetChildHolders") || OthersPatch(typeof(MapParent), "GetChildHolders")
                    || OthersPatch(typeof(MapParent), "GetDirectlyHeldThings") || OthersPatch(typeof(Settlement_TraderTracker), "GetChildHolders") ? 2 : 1;
            if (worldSafe != 1)
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

        // true when a mod other than `except` patches the method
        private static bool OthersPatch(Type t, string method, string except = null)
        {
            MethodInfo m = AccessTools.DeclaredMethod(t, method);
            if (m == null)
                return false;
            Patches p = Harmony.GetPatchInfo(m);
            if (p == null)
                return false;
            foreach (string owner in p.Owners)
                if (owner != except)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------
        // 6) Health tick of pawns without any hediff: the empty list was still copied every tick.
        // ------------------------------------------------------------------
        public static void PatchHealth(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)),
                prefix: new HarmonyMethod(typeof(Perf), nameof(HealthPrefix)));
        }

        public static bool HealthPrefix(Pawn_HealthTracker __instance)
        {
            if (!UnofficialPatchMod.OnHealth)
                return true;
            List<Hediff> l = __instance.hediffSet?.hediffs;
            return l == null || l.Count != 0;
        }

        // ------------------------------------------------------------------
        // 7) Raid steal check (mrbeverage). Every 2 s each raider priced every tree and rock
        //    chunk around it, and pricing an item with no cost list searched ALL recipes of the
        //    game (2 000+ with many mods) – the answer is always the same. The answer is now
        //    remembered per item kind (forgotten when the number of recipes changes).
        // ------------------------------------------------------------------
        private static readonly ConcurrentDictionary<BuildableDef, RecipeDef> recipeCache = new ConcurrentDictionary<BuildableDef, RecipeDef>();
        private static int recipeCount = -1;
        private static int stealSafe;

        public static void PatchSteal(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculableRecipe)),
                prefix: new HarmonyMethod(typeof(Perf), nameof(RecipePrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(Perf), nameof(RecipePostfix)) { priority = Priority.Last });
        }

        public static bool RecipePrefix(BuildableDef def, ref RecipeDef __result, out bool __state)
        {
            __state = false;
            if (!UnofficialPatchMod.OnSteal || def == null)
                return true;
            if (stealSafe == 0)   // another mod changes the answer? then never cache it
                stealSafe = OthersPatch(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculableRecipe), "bolek98.unofficialpatch") ? 2 : 1;
            if (stealSafe != 1)
                return true;
            int count = DefDatabase<RecipeDef>.DefCount;
            if (count != recipeCount)
            {
                recipeCache.Clear();
                recipeCount = count;
            }
            if (recipeCache.TryGetValue(def, out RecipeDef r))
            {
                __result = r;
                return false;
            }
            __state = true;   // let the game compute it, remember it in the postfix
            return true;
        }

        public static void RecipePostfix(BuildableDef def, RecipeDef __result, bool __state)
        {
            if (__state && def != null)
                recipeCache[def] = __result;
        }
    }
}
