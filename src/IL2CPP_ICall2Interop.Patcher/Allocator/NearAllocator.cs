using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace IL2CPP_ICall2Interop.Patcher.Allocator;

// inspired by Dobby NearMemoryAllocator
internal sealed class NearAllocator
{
    public readonly IMemorySupport MemSupport;

    private readonly List<LinearAllocator> subAllocators = [];

    private NearAllocator()
    {
        if (OperatingSystem.IsWindows())
            MemSupport = new MemorySupportWindows();
        else
            throw new NotImplementedException(Environment.OSVersion.ToString());
    }

    public nint AllocNear(nint nearTarget, nuint size, nuint align = 8)
    {
        long minAddr = Math.Max(0, (long)nearTarget - int.MaxValue);
        long maxAddr = (long)nearTarget - int.MinValue;

        foreach (var sub in subAllocators)
        {
            var peekAddr = sub.PeekAllocate(size, align);
            if (peekAddr == 0 || peekAddr < minAddr || peekAddr > maxAddr)
                continue;

            return sub.Allocate(size, align);
        }

        var newSize = size;
        var pagesAddr = MemSupport.AllocateInRange((nint)minAddr, (nint)maxAddr, ref newSize);
        if (pagesAddr == 0)
            return 0;

        var allocator = new LinearAllocator(pagesAddr, newSize);
        subAllocators.Add(allocator);

        return allocator.Allocate(size, align);
    }

    public static readonly NearAllocator Global = new();
}
