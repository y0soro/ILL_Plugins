using System;
using System.Runtime.CompilerServices;
using Iced.Intel;

namespace ILL_SliderUnlocker.NativeCode;

public static class IcedExtensions
{
    public static bool IsBranch(this FlowControl flowControl)
    {
        if (
            flowControl == FlowControl.UnconditionalBranch
            || flowControl == FlowControl.IndirectBranch
            || flowControl == FlowControl.ConditionalBranch
        )
            return true;

        return false;
    }

    public static bool IsRet(this FlowControl flowControl)
    {
        return flowControl == FlowControl.Return;
    }

    public static bool IsInterrupt(this FlowControl flowControl)
    {
        return flowControl == FlowControl.Interrupt;
    }

    public static bool IsConditional(this FlowControl flowControl)
    {
        return flowControl == FlowControl.ConditionalBranch;
    }

    public static bool HasImmediateBranchTarget(this Instruction instruction)
    {
        return instruction.Op0Kind switch
        {
            OpKind.FarBranch16
            or OpKind.FarBranch32
            or OpKind.NearBranch16
            or OpKind.NearBranch32
            or OpKind.NearBranch64
            or OpKind.Immediate16
            or OpKind.Immediate32
            or OpKind.Immediate32to64
            or OpKind.Immediate64
            or OpKind.Immediate8
            or OpKind.Immediate8to16
            or OpKind.Immediate8to32
            or OpKind.Immediate8to64
            or OpKind.Immediate8_2nd => true,
            _ => false,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetImmediateBranchTarget(this Instruction instruction)
    {
        return instruction.Op0Kind switch
        {
            OpKind.FarBranch16 => instruction.FarBranch16,
            OpKind.FarBranch32 => instruction.FarBranch32,
            OpKind.NearBranch16 => instruction.NearBranch16,
            OpKind.NearBranch32 => instruction.NearBranch32,
            OpKind.NearBranch64 => instruction.NearBranch64,
            OpKind.Immediate16
            or OpKind.Immediate32
            or OpKind.Immediate32to64
            or OpKind.Immediate64
            or OpKind.Immediate8
            or OpKind.Immediate8to16
            or OpKind.Immediate8to32
            or OpKind.Immediate8to64
            or OpKind.Immediate8_2nd => instruction.GetImmediate(0),
            _ => throw new InvalidOperationException(
                string.Format("Operand kind {0} is not a branch.", instruction.Op0Kind)
            ),
        };
    }
}
