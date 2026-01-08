using System;
using System.Collections.Generic;
using System.IO;
using Iced.Intel;

namespace ILL_SliderUnlocker.NativeCode;

public class NativeCodeBlockAnalyzer : IDisposable
{
    private UnmanagedMemoryStream stream;

    private readonly ulong imageBase;
    private readonly ulong imageEnd;

    public NativeCodeBlockAnalyzer(ulong imageBase, ulong imageSize)
    {
        this.imageBase = imageBase;
        this.imageEnd = imageBase + imageSize;
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

    public unsafe NativeCodeBlockCollection Walk(ulong rva)
    {
        Queue<ulong> toWalk = [];
        toWalk.Enqueue(imageBase + rva);

        var blocks = new NativeCodeBlockCollection();

        while (toWalk.TryDequeue(out var nextBlock))
        {
            if (blocks.ContainsAddress(nextBlock))
                continue;

            var nextParsedBlockIP = blocks.NextBlockIP(nextBlock);

            try
            {
                stream.PositionPointer = (byte*)nextBlock;
            }
            catch (Exception)
            {
                // Plugin.Log.LogError($"  block address out of range: {nextBlock:x}");
                continue;
            }

            var decoder = Decoder.Create(64, new StreamCodeReader(stream));
            decoder.IP = nextBlock;

            List<Instr> instructions = [];

            while (stream.Position < stream.Length)
            {
                decoder.Decode(out var instr);
                if (instr.IsInvalid || decoder.LastError == DecoderError.NoMoreBytes)
                    break;

                instructions.Add(new(instr));

                if (instr.FlowControl.IsRet() || instr.FlowControl.IsInterrupt())
                    break;

                if (instr.FlowControl == FlowControl.Call)
                {
                    if (instr.HasImmediateBranchTarget())
                    {
                        var target = instr.GetImmediateBranchTarget();
                        if (target >= imageBase && target < imageEnd)
                            blocks.AddCallTarget(target);
                    }
                }

                if (instr.FlowControl.IsBranch())
                {
                    if (instr.HasImmediateBranchTarget())
                    {
                        var target = instr.GetImmediateBranchTarget();
                        toWalk.Enqueue(target);
                    }

                    if (!instr.FlowControl.IsConditional())
                        break;
                }

                if (nextParsedBlockIP != 0 && instr.NextIP >= nextParsedBlockIP)
                    break;
            }

            // sniff filler NOPs
            while (stream.Position < stream.Length)
            {
                decoder.Decode(out var instr);

                if (instr.Mnemonic != Mnemonic.Nop)
                    break;

                if (nextParsedBlockIP != 0 && instr.IP >= nextParsedBlockIP)
                    break;

                instructions.Add(new(instr));

                if (nextParsedBlockIP != 0 && instr.NextIP >= nextParsedBlockIP)
                    break;
            }

            var block = new NativeCodeBlock([.. instructions]);

            try
            {
                blocks.AddBlock(block);
            }
            catch (Exception)
            {
                // Plugin.Log.LogError(
                //     $"  already exists: {block.IP - imageBase:x} - {block.NextIP - imageBase:x}"
                // );

                toWalk.Clear();
                continue;
            }

            // var sorted = toWalk.ToList();
            // sorted.Sort();
            // toWalk = new([.. sorted]);
        }

        return blocks;
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;

        GC.SuppressFinalize(this);
    }
}
