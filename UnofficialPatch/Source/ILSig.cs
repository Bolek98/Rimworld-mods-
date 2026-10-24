using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace UnofficialPatch
{
    // Build-independent fingerprint of a method body: opcodes, constants and the NAMES of the
    // methods/fields it uses (metadata tokens change with every game build, names do not).
    // When Ludeon changes a method a fix relies on, the fingerprint changes and the fix turns off.
    public static class ILSig
    {
        private static Dictionary<short, OpCode> ops;

        private static void Init()
        {
            if (ops != null) return;
            var d = new Dictionary<short, OpCode>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is OpCode o) d[o.Value] = o;
            ops = d;
        }

        // memberName(token) -> name of the method/field (or null); str(token) -> user string
        public static uint Hash(byte[] il, Func<int, string> memberName, Func<int, string> str)
        {
            Init();
            uint h = 2166136261;
            void Add(string s) { foreach (char c in s) { h ^= c; h *= 16777619; } h ^= 0x1F; h *= 16777619; }
            int i = 0;
            while (i < il.Length)
            {
                short v = il[i++];
                if (v == 0xFE) v = (short)(0xFE00 | il[i++]);
                if (!ops.TryGetValue(v, out OpCode op)) { Add("?" + v); continue; }
                string name = op.Name;
                if (name.EndsWith(".s")) name = name.Substring(0, name.Length - 2);
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: Add(name); break;
                    case OperandType.ShortInlineBrTarget: i += 1; Add(name); break;
                    case OperandType.InlineBrTarget: i += 4; Add(name); break;
                    case OperandType.ShortInlineI: Add(name + " " + (op.Value == OpCodes.Ldc_I4_S.Value ? (sbyte)il[i] : il[i])); i += 1; break;
                    case OperandType.ShortInlineVar: Add(name + " " + il[i]); i += 1; break;
                    case OperandType.InlineVar: Add(name + " " + BitConverter.ToUInt16(il, i)); i += 2; break;
                    case OperandType.InlineI: Add(name + " " + BitConverter.ToInt32(il, i)); i += 4; break;
                    case OperandType.InlineI8: Add(name + " " + BitConverter.ToInt64(il, i)); i += 8; break;
                    case OperandType.ShortInlineR: Add(name + " " + BitConverter.ToSingle(il, i).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); i += 4; break;
                    case OperandType.InlineR: Add(name + " " + BitConverter.ToDouble(il, i).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); i += 8; break;
                    case OperandType.InlineString: Add(name + " " + (str(BitConverter.ToInt32(il, i)) ?? "?")); i += 4; break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                        Add(name + " " + (memberName(BitConverter.ToInt32(il, i)) ?? "?")); i += 4; break;
                    case OperandType.InlineSwitch:
                        int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; Add(name + " " + n); break;
                    default: i += 4; Add(name); break; // type, tok, sig
                }
            }
            return h;
        }

        // At runtime the names come from the loaded game (Module.ResolveMember); the fingerprints in
        // Core.cs were computed from the game DLL's metadata with the same Hash function – both give
        // the plain member names (methods, fields, constructors, getters).
        public static uint Of(MethodBase m)
        {
            byte[] il = m.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return 0;
            Module mod = m.Module;
            Type[] ta = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            Type[] ma = m.IsGenericMethod ? m.GetGenericArguments() : null;
            return Hash(il,
                t => { try { return mod.ResolveMember(t, ta, ma)?.Name; } catch { return null; } },
                t => { try { return mod.ResolveString(t); } catch { return null; } });
        }
    }
}
