using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // ------------------------------------------------------------------
    // 1.5.0 Shorter save stop. The game writes the save straight into a file with a
    // 4 KB buffer while it walks through the whole game state – tens of thousands of
    // small disk writes. Here the file gets a 1 MB buffer: the same file, the same
    // content, only written in big pieces. Saving itself cannot move to the background
    // (the game state must not change while it is written), so this only shortens it.
    // The time of every save is measured and shown in the settings and the log.
    // ------------------------------------------------------------------
    public static class SaveBuffer
    {
        public const int BufferSize = 1 << 20;
        public static float LastMs = -1f, BestMs = -1f;
        public static int Saves;
        private static bool patched;

        public static void Patch(Harmony h)
        {
            try
            {
                MethodInfo init = AccessTools.Method(typeof(ScribeSaver), nameof(ScribeSaver.InitSaving));
                if (init != null)
                {
                    h.Patch(init, transpiler: new HarmonyMethod(typeof(SaveBuffer), nameof(Transpiler)));
                }
                MethodInfo save = AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), new[] { typeof(string) });
                if (save != null)
                    h.Patch(save, prefix: new HarmonyMethod(typeof(SaveBuffer), nameof(SavePrefix)), postfix: new HarmonyMethod(typeof(SaveBuffer), nameof(SavePostfix)));
            }
            catch (Exception e)
            {
                MLog.Warn("[Smooth FPS] save buffer skipped: " + e.Message);
            }
        }

        // new FileStream(path, mode, access, share) → Open(path, mode, access, share)
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            ConstructorInfo ctor = AccessTools.Constructor(typeof(FileStream), new[] { typeof(string), typeof(FileMode), typeof(FileAccess), typeof(FileShare) });
            MethodInfo open = AccessTools.Method(typeof(SaveBuffer), nameof(Open));
            foreach (CodeInstruction ci in code)
            {
                if (!patched && ctor != null && ci.opcode == OpCodes.Newobj && Equals(ci.operand, ctor))
                {
                    patched = true;
                    yield return new CodeInstruction(OpCodes.Call, open).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    continue;
                }
                yield return ci;
            }
        }

        public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.saveBuffer)
                return new FileStream(path, mode, access, share);
            return new FileStream(path, mode, access, share, BufferSize);
        }

        private static float started;

        public static void SavePrefix() => started = Time.realtimeSinceStartup;

        public static void SavePostfix()
        {
            try
            {
                LastMs = (Time.realtimeSinceStartup - started) * 1000f;
                if (BestMs < 0f || LastMs < BestMs) BestMs = LastMs;
                Saves++;
                if (Saves <= 5)
                    MLog.Info("[Smooth FPS] game saved in " + LastMs.ToString("0") + " ms" + (patched ? " (big write buffer)" : ""));
            }
            catch
            {
            }
        }

        public static bool Active => patched;
    }
}
