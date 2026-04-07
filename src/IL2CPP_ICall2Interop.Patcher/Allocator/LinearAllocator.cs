namespace IL2CPP_ICall2Interop.Patcher.Allocator;

internal class LinearAllocator
{
    public readonly nint heap;
    public readonly nint size;
    public readonly nint head_end;

    private nint cursor;

    public LinearAllocator(nint heap, nuint size)
    {
        this.heap = heap;
        this.size = (nint)size;
        head_end = heap + (nint)size;
        cursor = heap;
    }

    public nint PeekAllocate(nuint size, nuint align)
    {
        var addr = cursor.AlignNext(align);
        if (addr + (nint)size > head_end)
            return 0;

        return addr;
    }

    public nint Allocate(nuint size, nuint align)
    {
        var addr = cursor.AlignNext(align);

        var newCursor = addr + (nint)size;
        if (newCursor > head_end)
            return 0;

        cursor = newCursor;
        return addr;
    }
}
