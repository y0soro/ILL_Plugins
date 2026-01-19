using System;
using System.Collections.Generic;

namespace ILL_SliderUnlocker.NativeCode;

public class NativeCodeBlockCollection
{
    public readonly List<NativeCodeBlock> Blocks = [];
    public readonly HashSet<ulong> CallTargets = [];

    public void AddCallTarget(ulong address)
    {
        CallTargets.Add(address);
    }

    public void AddBlock(NativeCodeBlock block)
    {
        var blockIp = block.IP;

        var mergeBefore = false;
        var mergeAfter = false;

        var targetIdx = -1;
        for (int i = -1; i < Blocks.Count; i++)
        {
            NativeCodeBlock curr = i >= 0 ? Blocks[i] : null;

            if (curr == null || curr.NextIP <= blockIp)
            {
                NativeCodeBlock next = i + 1 < Blocks.Count ? Blocks[i + 1] : null;

                if (next == null || next.IP >= block.NextIP)
                {
                    mergeBefore = curr != null && curr.NextIP == blockIp;
                    mergeAfter = next != null && next.IP == block.NextIP;

                    targetIdx = i + 1;
                    break;
                }
            }
        }

        if (targetIdx == -1)
        {
            throw new ArgumentException("block instructions already exist");
        }

        if (!mergeBefore && !mergeAfter)
        {
            Blocks.Insert(targetIdx, block);
        }
        else if (mergeBefore)
        {
            Blocks[targetIdx - 1].Merge(block);
            if (mergeAfter)
            {
                var after = Blocks[targetIdx];
                Blocks.RemoveAt(targetIdx);
                Blocks[targetIdx - 1].Merge(after);
            }
        }
        else if (mergeAfter)
        {
            block.Merge(Blocks[targetIdx]);
            Blocks[targetIdx] = block;
        }
    }

    public bool ContainsAddress(ulong address)
    {
        foreach (var block in Blocks)
        {
            if (address >= block.IP && address < block.NextIP)
            {
                return true;
            }
        }
        return false;
    }

    public ulong NextBlockIP(ulong address)
    {
        foreach (var block in Blocks)
        {
            if (block.IP >= address)
            {
                return block.IP;
            }
        }

        return 0;
    }
}
