using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace IL2CPP_ICall2Interop.Patcher;

internal class ICallProxy
{
    [ThreadStatic]
    private int HookDepth = 0;

    public bool ManagedPatched { get; private set; } = false;

    public nint OriginalICall { get; set; } = 0;
    public nint ResolvedICall { get; set; } = 0;

    // public int callCount = 0;

    private readonly MethodBase method;

    // private Stopwatch stopWatch = new();

    public ICallProxy(MethodBase method)
    {
        this.method = method;
    }

    public void SetManagedPatched()
    {
        ManagedPatched = true;
    }

    public void EnterManaged()
    {
        HookDepth++;

        // Patcher.Log.LogDebug(
        //     $"EnterManaged, Depth {HookDepth}, {method.DeclaringType.FullName}::{method.Name} ICall: 0x{OriginalICall:x}"
        // );
    }

    public void ExitManaged()
    {
        // Patcher.Log.LogDebug(
        //     $"ExitManaged, Depth {HookDepth}, {method.DeclaringType.FullName}::{method.Name} ICall: 0x{OriginalICall:x}"
        // );
        HookDepth--;
    }

    public bool IsInManaged()
    {
        return HookDepth >= 0;
    }

    private unsafe nint UseOriginalICall()
    {
        // if (callCount == 0)
        // {
        //     stopWatch.Start();
        // }
        // if (callCount % 10000 == 0)
        // {
            // var elapsed = stopWatch.Elapsed;
            // Patcher.Log.LogDebug(
            //     $"Depth {HookDepth}, {method.DeclaringType.FullName}::{method.Name} ICall: 0x{OriginalICall:x}, count:{callCount}, rate:{(float)callCount / elapsed.Seconds}"
            // );
        // }

        // callCount++;

        if (ManagedPatched)
        {
            if (HookDepth == 0)
                return 0; // call managed
        }
        else
        {
            // if (HookDepth++ == 0)
            //     return 0; // call managed
            // HookDepth = 0;
        }

        if (OriginalICall == 0)
        {
            throw new Exception("OriginalICall not set");
        }

        return OriginalICall;
    }

    public static void EnterManagedFromHandle(nint ptr)
    {
        ((ICallProxy)GCHandle.FromIntPtr(ptr).Target).EnterManaged();
    }

    public static void ExitManagedFromHandle(nint ptr)
    {
        ((ICallProxy)GCHandle.FromIntPtr(ptr).Target).ExitManaged();
    }

    public static nint UseOriginalICallFromHandle(nint ptr)
    {
        return ((ICallProxy)GCHandle.FromIntPtr(ptr).Target).UseOriginalICall();
    }
}
