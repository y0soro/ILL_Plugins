using System;
using System.Collections.Generic;
using Iced.Intel;

namespace ILL_SliderUnlocker.NativeCode;

public class NativeCodeBlock
{
    public Instr[] Instructions;

    public ulong IP => Instructions[0].IP;
    public ulong NextIP => Instructions[^1].NextIP;

    public NativeCodeBlock(Instr[] instructions)
    {
        if (instructions.Length < 1)
            throw new ArgumentException("Empty instruction set");
        Instructions = instructions;
    }

    public void Merge(NativeCodeBlock other)
    {
        if (NextIP != other.IP)
            throw new ArgumentException("Not a continuous block");

        Instructions = [.. Instructions, .. other.Instructions];
    }
}
