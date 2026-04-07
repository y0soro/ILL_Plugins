namespace IL2CPP_ICall2Interop.Patcher.Allocator;

// TODO: permission
internal interface IMemorySupport
{
    public nuint AllocPageSize();

    public nuint PageSize();

    public nint Allocate(nint address, nuint size);

    public nint AllocateInRange(nint minAddress, nint maxAddress, ref nuint size);

    public void Free(nint address, nuint size);

    public void SetExec(nint address, nuint size, bool writable);
}
