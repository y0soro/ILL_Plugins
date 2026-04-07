using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IL2CPP_ICall2Interop.Patcher.Allocator;

internal class MemorySupportWindows : IMemorySupport
{
    public nint Allocate(nint address, nuint size)
    {
        Debug.Assert(Utils.IsAligned(address, AllocPageSize()));

        var res = VirtualAlloc(
            address,
            size,
            AllocationType.Commit | AllocationType.Reserve,
            MemoryProtection.NoAccess
        );
        if (res == 0)
            return 0;

        return res;
    }

    public unsafe nint AllocateInRange(nint minAddr, nint maxAddr, ref nuint sizeOut)
    {
        if (sizeOut == 0)
            sizeOut = PageSize();

        var size = Utils.AlignNext((nint)sizeOut, PageSize());

        var align = AllocPageSize();
        minAddr = minAddr.AlignNext(align);
        maxAddr = maxAddr.AlignCurr(align);

        nint nextRegion = 0;

        var regionCnt = 0;
        while (nextRegion <= maxAddr)
        {
            if (
                VirtualQuery(nextRegion, out var region, (nuint)sizeof(MEMORY_BASIC_INFORMATION))
                == 0
            )
                return 0;
            regionCnt++;

            nextRegion = region.BaseAddress + region.RegionSize;

            if (region.State != RegionState.Free)
                continue;

            var address = region.BaseAddress.AlignNext(align);

            // allocate in the middle of region if possible
            if (address < minAddr)
                address = minAddr;

            // remainingSize < 0 if address/minAddr is not in region
            var remainingSize = region.RegionSize - (address - region.BaseAddress);
            if (size > remainingSize)
                continue;

            // allocate whole allocation granularity to avoid wasting
            if (remainingSize >= (nint)align)
                size = (nint)align;

            var res = Allocate(address, (nuint)size);
            if (res == 0)
                throw new InvalidOperationException("Failed to allocate free pages");
            if (res != address)
                throw new InvalidOperationException(
                    $"Allocated address not equal, expected:{address:x}, actual:{res:x}"
                );

            // Console.Error.WriteLine(
            //     $"allocated near:{address:x}, size:{size}, iteratedRegions:{regionCnt}"
            // );

            sizeOut = (nuint)size;
            return res;
        }

        return 0;
    }

    public void Free(nint address, nuint size)
    {
        VirtualFree(address, size, FreeType.Release);
    }

    public void SetExec(nint address, nuint size, bool writable)
    {
        var align = PageSize();
        var end = (address + (nint)size).AlignNext(align);

        address = address.AlignCurr(align);
        size = (nuint)(end - address);

        VirtualProtect(
            address,
            size,
            writable ? MemoryProtection.ExecuteReadWrite : MemoryProtection.ExecuteRead,
            out _
        );
    }

    public nuint AllocPageSize()
    {
        return 0x10000;
    }

    public nuint PageSize()
    {
        return 0x1000;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public RegionState State;
        public MemoryProtection Protect;
        public uint Type;
    }

    [Flags]
    public enum RegionState
    {
        Commit = 0x1000,
        Reserve = 0x2000,
        Free = 0x10000,
    }

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

    [Flags]
    public enum FreeType
    {
        Release = 0x00008000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(
        nint lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        nuint dwLength
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(
        nint lpAddress,
        nuint dwSize,
        AllocationType flAllocationType,
        MemoryProtection flProtect
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint lpAddress, nuint dwSize, FreeType dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(
        nint lpAddress,
        nuint dwSize,
        MemoryProtection flNewProtect,
        out MemoryProtection lpflOldProtect
    );
}
