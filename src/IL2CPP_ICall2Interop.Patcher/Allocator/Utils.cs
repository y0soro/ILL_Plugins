using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace IL2CPP_ICall2Interop.Patcher.Allocator;

internal static class Utils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(this nuint align)
    {
        return (align & (align - 1)) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAligned(this nint address, nuint align)
    {
        Debug.Assert(align.IsPowerOfTwo());
        var mask = (nint)align - 1;
        return (address & mask) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint AlignCurr(this nint address, nuint align)
    {
        Debug.Assert(align.IsPowerOfTwo());
        var mask = (nint)align - 1;
        return address & ~mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint AlignNext(this nint address, nuint align)
    {
        Debug.Assert(align.IsPowerOfTwo());
        var mask = (nint)align - 1;
        return (address + mask) & ~mask;
    }
}
