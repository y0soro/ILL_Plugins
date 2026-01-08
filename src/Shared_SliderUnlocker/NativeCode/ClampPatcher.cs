using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iced.Intel;

namespace ILL_SliderUnlocker.NativeCode;

/// <summary>
/// x64 only
/// </summary>
public class ClampPatcher
{
    private UnmanagedMemoryStream stream;

    private readonly ulong imageBase;

    public ClampPatcher(ulong imageBase, ulong imageSize)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Not x64 architecture.");

        this.imageBase = imageBase;
        unsafe
        {
            stream = new UnmanagedMemoryStream(
                (byte*)imageBase,
                (long)imageSize,
                (long)imageSize,
                FileAccess.ReadWrite
            );
        }
    }

    public unsafe bool PreCheck(NativeCodeBlock block)
    {
        var hasComiss = false;
        var hasClampBoundValue = false;

        foreach (var instr in block.Instructions)
        {
            if (!hasComiss && ClampComiss(instr))
            {
                hasComiss = true;
            }

            if (
                !hasClampBoundValue
                && instr.Mnemonic == Mnemonic.Movss
                && instr.Op0Kind == OpKind.Register
                && instr.Op1Kind == OpKind.Memory
                && instr.Inner.IsIPRelativeMemoryOperand
            )
            {
                try
                {
                    stream.PositionPointer = (byte*)instr.Inner.IPRelativeMemoryAddress;

                    var buf = new byte[4];
                    stream.Read(buf);

                    var bound = BitConverter.ToSingle(buf);

                    if (bound == 1f)
                        hasClampBoundValue = true;
                }
                catch (Exception) { }
            }

            if (hasComiss && hasClampBoundValue)
                return true;
        }

        return false;
    }

    public IEnumerable<Instruction> Patch(NativeCodeBlock block)
    {
        var len = block.Instructions.Length;
        for (int i = 0; i < len; )
        {
            if (!MatchClamp(block.Instructions[i..], out var clampLen, out var patches))
            {
                i++;
                continue;
            }

            foreach (var patch in patches)
                yield return patch;

            i += clampLen;
        }
    }

    private bool MatchClamp(
        IEnumerable<Instr> instructions,
        out int len,
        out IEnumerable<Instruction> patches
    )
    {
        var count = 0;
        patches = [];
        var it = instructions.GetEnumerator();

        Instr prev = null;
        Instr curr = null;

        len = 0;

        bool Next()
        {
            if (!it.MoveNext())
            {
                curr = null;
                return false;
            }

            prev = curr;
            curr = it.Current;
            count++;
            return true;
        }

        bool NextUntil(Func<Instr, bool> filter, int limit = 10)
        {
            for (int i = 0; i < limit; i++)
            {
                if (!Next())
                    return false;
                if (filter(curr))
                    return true;
            }

            return false;
        }

        bool NextCheckUntil(Func<Instr, bool> check, Func<Instr, bool> filter, int limit = 10)
        {
            for (int i = 0; i < limit; i++)
            {
                if (!Next() || !check(curr))
                    return false;

                if (filter(curr))
                    return true;
            }

            return false;
        }

        // COMISS lower, value
        if (!Next() || !ClampComiss(curr))
            return false;

        var comp0Reg0 = curr.Op0Register;
        var comp0Reg1 = curr.Op1Register;

        // JA set_lower
        if (!NextUntil(FlowBreak) || !ClampJa(curr))
            return false;

        // Plugin.Log.LogDebug("");
        // Plugin.Log.LogDebug("comiss");
        // Plugin.Log.LogDebug($"ja {curr.Op0Kind} {curr.IP - imageBase:x}");

        var br0Instr = curr;
        var br0Target = curr.NearBranchTarget;

        // Plugin.Log.LogDebug("comiss");
        // COMISS value, upper
        if (!NextCheckUntil(FlowContinue, ClampComiss))
            return false;

        var comp1Reg0 = curr.Op0Register;
        var comp1Reg1 = curr.Op1Register;

        // check value register
        if (comp0Reg1 != comp1Reg0)
        {
            return false;
        }

        // Plugin.Log.LogDebug("(ja/jbe)");

        //  JBE finish
        //  MOVAPS value, upper
        //  JMP finish
        // set_lower:
        //  XORPS value, value
        // finish:
        //
        //or
        //
        //  JA assign_upper
        //  MOVAPS target, value
        //  JMP finish
        // assign_upper:
        //  MOVAPS target, upper
        //  JMP finish
        // set_lower:
        // assign_lower:
        //  XORPS target, target
        // finish:
        if (!NextUntil(FlowBreak) || !ClampJa(curr) && !ClampJbe(curr))
            return false;

        var br1Instr = curr;
        var br1IsJbe = curr.Mnemonic == Mnemonic.Jbe;
        var br1Target = curr.NearBranchTarget;

        if (br1IsJbe)
        {
            // Plugin.Log.LogDebug("jbe");
            // Plugin.Log.LogDebug("movaps");
            if (!NextCheckUntil(FlowContinue, ClampMovaps))
                return false;
            var movUpperReg0 = curr.Op0Register;
            var movUpperReg1 = curr.Op1Register;

            // Plugin.Log.LogDebug("jmp");
            if (!NextUntil(FlowBreak) || !ClampJmp(curr))
                return false;
            var br2Target = curr.NearBranchTarget;

            // check upper register
            if (comp1Reg1 != movUpperReg1)
            {
                // Plugin.Log.LogDebug($" jbe reg not equal:{curr.IP - imageBase:x}");
                return false;
            }

            var br0Patch = br0Instr.Inner;
            br0Patch.Code = JccToJmp(br0Patch.Code);
            br0Patch.NearBranch64 = br0Patch.NextIP;

            var br1Patch = br1Instr.Inner;
            br1Patch.Code = JccToJmp(br1Patch.Code);

            patches = [br0Patch, br1Patch];

            // if (br1Target != br2Target)
            // {
            //     Plugin.Log.LogDebug($" jbe br not equal:{curr.IP - imageBase:x}");
            // }
        }
        else
        {
            // Plugin.Log.LogDebug("ja");
            // Plugin.Log.LogDebug("movaps");
            if (!NextCheckUntil(FlowContinue, ClampMovaps))
                return false;
            var movAssignReg0 = curr.Op0Register;
            var movAssignReg1 = curr.Op1Register;

            // check value register
            if (comp1Reg0 != movAssignReg1)
                return false;

            // Plugin.Log.LogDebug("jmp");
            if (!NextUntil(FlowBreak) || !ClampJmp(curr))
                return false;
            var br2Target = curr.NearBranchTarget;

            // // there could be assign_upper block if not equal
            // if (br1Target != br2Target)
            // {
            //     // Plugin.Log.LogDebug("movaps");
            //     if (!NextCheckUntil(FlowContinue, ClampMovaps))
            //         return false;
            //     var movUpperReg0 = curr.Op0Register;
            //     var movUpperReg1 = curr.Op1Register;

            //     // check target register
            //     if (movAssignReg0 != movUpperReg0)
            //     {
            //         // Plugin.Log.LogDebug($" ja reg not equal:{curr.IP - imageBase:x}");
            //         return false;
            //     }
            // }

            var br0Patch = br0Instr.Inner;
            br0Patch.Code = JccToJmp(br0Patch.Code);
            br0Patch.NearBranch64 = br0Patch.NextIP;

            var br1Patch = br1Instr.Inner;
            br1Patch.Code = JccToJmp(br1Patch.Code);
            br1Patch.NearBranch64 = br1Patch.NextIP;

            patches = [br0Patch, br1Patch];
        }

        len = count;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FlowContinue(Instr i)
    {
        return i.FlowControl == FlowControl.Next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FlowBreak(Instr i)
    {
        return i.FlowControl != FlowControl.Next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClampComiss(Instr i)
    {
        return i.Mnemonic == Mnemonic.Comiss
            && i.Op0Kind == OpKind.Register
            && i.Op1Kind == OpKind.Register;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClampJa(Instr i)
    {
        return i.Mnemonic == Mnemonic.Ja && i.Op0Kind == OpKind.NearBranch64;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClampJbe(Instr i)
    {
        return i.Mnemonic == Mnemonic.Jbe && i.Op0Kind == OpKind.NearBranch64;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClampJmp(Instr i)
    {
        return i.Mnemonic == Mnemonic.Jmp && i.Op0Kind == OpKind.NearBranch64;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClampMovaps(Instr i)
    {
        return i.Mnemonic == Mnemonic.Movaps
            && i.Op0Kind == OpKind.Register
            && i.Op1Kind == OpKind.Register;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Code JccToJmp(Code code)
    {
        return code switch
        {
            Code.Ja_rel32_64 or Code.Jbe_rel32_64 => Code.Jmp_rel32_64,
            Code.Ja_rel8_64 or Code.Jbe_rel8_64 => Code.Jmp_rel8_64,
            _ => throw new NotImplementedException(string.Format("Unhandled code {0}.", code)),
        };
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;

        GC.SuppressFinalize(this);
    }
}
