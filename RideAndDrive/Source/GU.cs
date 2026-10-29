using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RideDrive
{
    // Giddy-Up, reached by name. Its 1.7 build is made for a newer runtime library than the game's own
    // reference files, so this mod is not compiled against it – every call goes through here.
    public static class GU
    {
        public static Type tMountUtility, tStorageUtility, tStorage, tData, tTexture, tSettings, tSetup, tMounted, tIsMountable, tMountable, tNotMountable;
        private static FieldInfo fIsMounted, fOffset, fMountableCache, fInvert, fNoMountedHunting;
        private static MethodInfo mGetData, mIsMounted, mDismount, mIsMountable, mFormatKey, mSetDrawOffset, mSetAllowedJob, mBuildAllowed, mGoMount, mHasExt;
        private static PropertyInfo pMount, pRider, pReservedMount;
        private static Type tGiveJob;
        public static JobDef MountJob;

        public static bool Init()
        {
            tMountUtility = AccessTools.TypeByName("GiddyUp.MountUtility");
            if (tMountUtility == null) return false;
            tStorageUtility = AccessTools.TypeByName("GiddyUp.StorageUtility");
            tStorage = AccessTools.TypeByName("GiddyUp.ExtendedDataStorage");
            tData = AccessTools.TypeByName("GiddyUp.ExtendedPawnData");
            tTexture = AccessTools.TypeByName("GiddyUp.TextureUtility");
            tSettings = AccessTools.TypeByName("GiddyUp.ModSettings_GiddyUp");
            tSetup = AccessTools.TypeByName("GiddyUp.Setup");
            tMounted = AccessTools.TypeByName("GiddyUp.Jobs.JobDriver_Mounted");
            tIsMountable = AccessTools.TypeByName("GiddyUp.IsMountableUtility");
            tMountable = AccessTools.TypeByName("GiddyUp.Mountable");
            tNotMountable = AccessTools.TypeByName("GiddyUp.NotMountable");
            fIsMounted = AccessTools.Field(tStorage, "isMounted");
            fOffset = AccessTools.Field(tSettings, "offsetCache");
            fMountableCache = AccessTools.Field(tSettings, "MountableCache");
            fInvert = AccessTools.Field(tSettings, "invertMountingRules");
            fNoMountedHunting = AccessTools.Field(tSettings, "noMountedHunting");
            mGetData = AccessTools.Method(tStorageUtility, "GetExtendedPawnData", new[] { typeof(Pawn) });
            mIsMounted = AccessTools.Method(tStorageUtility, "IsMounted", new[] { typeof(Thing) });
            mDismount = AccessTools.Method(tMountUtility, "Dismount");
            mGoMount = AccessTools.Method(tMountUtility, "GoMount");
            tGiveJob = mGoMount?.GetParameters()[2].ParameterType;
            foreach (MethodInfo m in tIsMountable.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "IsMountable" && m.GetParameters().Length == 7) mIsMountable = m;
            mFormatKey = AccessTools.Method(tTexture, "FormatKey");
            mSetDrawOffset = AccessTools.Method(tTexture, "SetDrawOffset");
            mSetAllowedJob = AccessTools.Method(tMounted, "SetAllowedJob");
            mBuildAllowed = AccessTools.Method(tMounted, "BuildAllowedJobsCache");
            mHasExt = AccessTools.Method(typeof(Def), nameof(Def.HasModExtension));
            pMount = AccessTools.Property(tData, "Mount");
            pRider = AccessTools.Property(tData, "Rider");
            pReservedMount = AccessTools.Property(tData, "ReservedMount");
            MountJob = DefDatabase<JobDef>.GetNamedSilentFail("Mount");
            return mGetData != null && mIsMounted != null && mDismount != null && mGoMount != null && pMount != null;
        }

        public static object Data(Pawn p) => p == null ? null : mGetData.Invoke(null, new object[] { p });
        public static bool IsMounted(Pawn p) => p != null && (bool)mIsMounted.Invoke(null, new object[] { p });
        public static Pawn MountOf(Pawn rider) => pMount.GetValue(Data(rider)) as Pawn;
        public static Pawn ReservedMount(Pawn rider) => pReservedMount?.GetValue(Data(rider)) as Pawn;

        // a half-done mounting is undone: the rider is no longer on the animal
        public static void ClearMount(Pawn rider, Pawn animal)
        {
            var set = fIsMounted?.GetValue(null) as HashSet<int>;
            set?.Remove(rider.thingIDNumber);
            object d = Data(rider);
            if (d != null && pMount.GetValue(d) == animal) pMount.SetValue(d, null);
            if (animal != null && pRider != null)
            {
                object a = Data(animal);
                if (a != null && pRider.GetValue(a) == rider) pRider.SetValue(a, null);
            }
        }

        public static void Dismount(Pawn rider, Pawn animal, bool clearReservation, IntVec3 park, bool rope, bool waitForRider) =>
            mDismount.Invoke(null, new object[] { rider, animal, Data(rider), clearReservation, park, rope, waitForRider });

        // 0 Inject (as the next job), 1 Try (a player's order), 2 Instant
        public const int Inject = 0, TryOrder = 1, Instant = 2;

        public static void GoMount(Pawn rider, Pawn animal, int how) =>
            mGoMount.Invoke(null, new object[] { rider, animal, Enum.ToObject(tGiveJob, how), null, null });

        public static bool IsMountable(Pawn animal, Pawn rider)
        {
            if (mIsMountable == null) return true;
            return (bool)mIsMountable.Invoke(null, new object[] { animal, null, rider, true, true, true, null });
        }

        public static void AllowWhileMounted(JobDef j)
        {
            if (j != null && mSetAllowedJob != null) mSetAllowedJob.Invoke(null, new object[] { j, false });
        }

        // ---------- repair ----------
        public static bool SetupFails()
        {
            try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(tSetup.TypeHandle); return false; }
            catch { return true; }
        }

        public static int RebuildMountable()
        {
            var cache = fMountableCache?.GetValue(null) as HashSet<ushort>;
            if (cache == null) return 0;
            var inv = fInvert?.GetValue(null) as HashSet<string>;
            int n = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.race == null || !d.race.Animal || d.IsCorpse) continue;
                bool ok = d.race.baseBodySize > 1.2f;
                if (Has(d, tNotMountable)) ok = false;
                else if (Has(d, tMountable)) ok = true;
                if (inv != null && inv.Contains(d.defName)) ok = !ok;
                if (ok && cache.Add(d.shortHash)) n++;
            }
            try { mBuildAllowed?.Invoke(null, new object[] { fNoMountedHunting != null && (bool)fNoMountedHunting.GetValue(null) }); } catch { }
            return n;
        }

        private static bool Has(Def d, Type ext)
        {
            if (ext == null || d.modExtensions == null) return false;
            foreach (DefModExtension e in d.modExtensions)
                if (ext.IsInstanceOfType(e)) return true;
            return false;
        }

        public static Dictionary<string, float> Offsets()
        {
            var o = fOffset?.GetValue(null) as Dictionary<string, float>;
            if (o == null && fOffset != null)
            {
                o = new Dictionary<string, float>();
                fOffset.SetValue(null, o);
            }
            return o;
        }

        public static void EnsureOffset(Pawn animal)
        {
            var cache = Offsets();
            if (cache == null || animal?.kindDef == null) return;
            int age = animal.ageTracker?.CurLifeStageIndex ?? 0;
            string key = (string)mFormatKey.Invoke(null, new object[] { animal.kindDef, age });
            if (cache.ContainsKey(key)) return;
            float v = 0f;
            List<PawnKindLifeStage> st = animal.kindDef.lifeStages;
            if (st != null && age >= 0 && age < st.Count && st[age] != null)
            {
                try { v = (mSetDrawOffset.Invoke(null, new object[] { st[age] }) as float?) ?? 0f; } catch { }
            }
            cache[key] = v;
        }
    }
}
