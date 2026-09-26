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

    // HACK: sniff deadblocks, if merge then also mask with Int3
    public void SniffAndMaskDeadBlocks(ulong imageBase)
    {
        List<NativeCodeBlock> toAdd = [];
        for (int i = 1; i < Blocks.Count; i++)
        {
            var prevBlock = Blocks[i - 1];
            var currBlock = Blocks[i];
            
            if (prevBlock.IsTerminated)
                continue;

            var prevLastInstr = prevBlock.Instructions[^1];
            var currFirstInstr = currBlock.Instructions[0];
            var gap = currFirstInstr.IP - prevLastInstr.NextIP;

            if (
                prevLastInstr.FlowControl == Iced.Intel.FlowControl.UnconditionalBranch
                && prevLastInstr.Inner.HasImmediateBranchTarget()
                && prevLastInstr.Inner.GetImmediateBranchTarget() == currFirstInstr.IP
                && gap < 0x200
            )
            {
                List<Instr> list = [];

                var deadBlockIP = prevLastInstr.NextIP;

                for (ulong j = 0; j < gap; j++)
                {
                    var instr = Iced.Intel.Instruction.Create(Iced.Intel.Code.Int3);
                    instr.Length = 1;
                    instr.IP = deadBlockIP + j;
                    list.Add(new(instr));
                }

                var maskedBlock = new NativeCodeBlock([.. list], false);
                toAdd.Add(maskedBlock);
            }
        }

        foreach (var block in toAdd)
        {
            AddBlock(block);
        }
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
                    mergeBefore = curr != null && curr.NextIP == blockIp && !curr.IsTerminated;
                    mergeAfter = next != null && next.IP == block.NextIP && !block.IsTerminated;

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
