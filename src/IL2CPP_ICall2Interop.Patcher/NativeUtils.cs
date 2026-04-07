#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using IL2CPP_ICall2Interop.Patcher.Allocator;

namespace IL2CPP_ICall2Interop.Patcher;

internal static class NativeUtils
{
    public static unsafe nint CreateJmpTrampoline(nint jmpTo, bool forceIndirectJmp = false)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new NotImplementedException(RuntimeInformation.ProcessArchitecture.ToString());
        }

        // reserving more bytes for native patching
        //
        // Dobby detour will relocate at least 14 bytes of original function top, 16 bytes here for alignment,
        // we reserve the area and fill it with NOPs to avoid Dobby relocating our JMP near
        // that could be broken if relocated offset is out of int32 bound
        var prependNop = 16;
        var funcSize = prependNop + 16;
        var headSize = 5 + prependNop;

        var allocator = NearAllocator.Global;

        var trampoline = allocator.AllocNear(jmpTo - headSize, (nuint)funcSize);

        // this should never hit, address that is too far could cause Dobby failing to prepare hook
        if (trampoline == 0)
        {
            throw new OutOfMemoryException(
                $"Failed to allocate memory for trampoline at address nearing {jmpTo:x}"
            );
        }

        var offset = (long)jmpTo - trampoline - headSize;

        byte[] jmpInstruction;

        if (!forceIndirectJmp && offset >= int.MinValue && offset <= int.MaxValue)
        {
            // 5 bytes
            jmpInstruction =
            [
                // JMP near
                0xe9,
                (byte)(offset & 0xff),
                (byte)((offset >> 8) & 0xff),
                (byte)((offset >> 16) & 0xff),
                (byte)((offset >> 24) & 0xff),
            ];
        }
        else
        {
            // 14 bytes + 2 bytes of gap
            jmpInstruction =
            [
                // JMP indirect, qword ptr [RIP+<gap>]
                0xff,
                0x25,
                // gap 2
                2,
                0,
                0,
                0,
                // gap, fill with 0xcc(INT3) to instruct code ending
                0xcc,
                0xcc,
                // 64 bit absolute address
                (byte)(jmpTo & 0xff),
                (byte)((jmpTo >> 8) & 0xff),
                (byte)((jmpTo >> 16) & 0xff),
                (byte)((jmpTo >> 24) & 0xff),
                (byte)((jmpTo >> 32) & 0xff),
                (byte)((jmpTo >> 40) & 0xff),
                (byte)((jmpTo >> 48) & 0xff),
                (byte)((jmpTo >> 56) & 0xff),
            ];
        }

        allocator.MemSupport.SetExec(trampoline, (nuint)funcSize, true);

        using var stream = new UnmanagedMemoryStream(
            (byte*)trampoline,
            funcSize,
            funcSize,
            FileAccess.Write
        );

        for (int i = 0; i < prependNop; i++)
        {
            stream.WriteByte(0x90);
        }

        stream.Write(jmpInstruction);

        for (int i = (int)stream.Position; i < funcSize; i++)
        {
            stream.WriteByte(0xcc);
        }

        allocator.MemSupport.SetExec(trampoline, (nuint)funcSize, false);

        return trampoline;
    }
}
