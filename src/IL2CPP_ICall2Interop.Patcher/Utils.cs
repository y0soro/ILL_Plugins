using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace IL2CPP_ICall2Interop.Patcher;

internal static class Utils
{
    [Flags]
    public enum AllocationType
    {
        Commit = 0x1000,
        Reserve = 0x2000,
        Decommit = 0x4000,
        Release = 0x8000,
        Reset = 0x80000,
        Physical = 0x400000,
        TopDown = 0x100000,
        WriteWatch = 0x200000,
        LargePages = 0x20000000,
    }

    [Flags]
    public enum MemoryProtection
    {
        Execute = 0x10,
        ExecuteRead = 0x20,
        ExecuteReadWrite = 0x40,
        ExecuteWriteCopy = 0x80,
        NoAccess = 0x01,
        ReadOnly = 0x02,
        ReadWrite = 0x04,
        WriteCopy = 0x08,
        GuardModifierflag = 0x100,
        NoCacheModifierflag = 0x200,
        WriteCombineModifierflag = 0x400,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint VirtualAlloc(
        nint lpAddress,
        uint dwSize,
        AllocationType lAllocationType,
        MemoryProtection flProtect
    );

    public static unsafe nint CreateJmpTrampoline(nint jmpTo)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new NotImplementedException(RuntimeInformation.ProcessArchitecture.ToString());
        }

        // Reserving more bytes for native patching.
        // Dobby detour would need at least 16 bytes of original function size, we allocate 32 bytes here just in case.
        uint funcSize = 32;

        nint trampoline = VirtualAlloc(
            0,
            funcSize,
            AllocationType.Commit | AllocationType.Reserve,
            MemoryProtection.ExecuteReadWrite
        );
        if (trampoline == 0)
        {
            throw new Exception("Failed to allocate memory for trampoline.");
        }

        var offset = jmpTo - trampoline - 5;

        byte[] jmpInstruction;
        if (offset > int.MaxValue || offset < int.MinValue)
        {
            byte[] jmpi =
            [
                // JMP indirect, qword ptr [RIP+<gap>]
                0xff,
                0x25,
                0,
                0,
                0,
                0,
            ];
            // NOPs in middle
            byte[] datJmpTo =
            [
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

            // reserved for native patching
            var gap = funcSize - jmpi.Length - datJmpTo.Length;
            if (gap > byte.MaxValue || gap < 0)
            {
                throw new Exception("invalid rel8");
            }

            jmpi[2] = (byte)(gap & 0xff);
            jmpi[3] = (byte)(gap >> 8 & 0xff);
            jmpi[4] = (byte)(gap >> 16 & 0xff);
            jmpi[5] = (byte)(gap >> 24 & 0xff);

            jmpInstruction = [.. jmpi, .. Enumerable.Repeat((byte)0x90, (int)gap), .. datJmpTo];
        }
        else
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

        using var stream = new UnmanagedMemoryStream(
            (byte*)trampoline,
            funcSize,
            funcSize,
            FileAccess.Write
        );

        stream.Write(jmpInstruction);

        return trampoline;
    }

    public static byte[] GetIlAsByteArray(this DynamicMethod dynMethod)
    {
        byte[] il =
            dynMethod
                .GetILGenerator()
                .GetType()
                .GetMethod("BakeByteArray", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(dynMethod.GetILGenerator(), null) as byte[];
        return il;
    }
}
