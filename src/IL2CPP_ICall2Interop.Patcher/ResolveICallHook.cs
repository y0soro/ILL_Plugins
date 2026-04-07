using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using IL2CPP_ICall2Interop.Patcher;

internal static class ResolveICallHook
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ICallResolveDelegate([MarshalAs(UnmanagedType.LPStr)] string name);

    private static INativeDetour detour = null;

    private static ICallResolveDelegate OrigResolveICall;

    private static readonly ConcurrentDictionary<string, nint> iCallTrampolines = [];

    public static void Install()
    {
        detour?.Free();

        var Il2CppHandle = NativeLibrary.Load(
            "GameAssembly",
            typeof(ResolveICallHook).Assembly,
            null
        );

        NativeLibrary.TryGetExport(
            Il2CppHandle,
            "il2cpp_resolve_icall",
            out var il2cpp_resolve_icall
        );

        detour = INativeDetour.CreateAndApply(
            il2cpp_resolve_icall,
            ResolveICall,
            out OrigResolveICall
        );
    }

    // some internal calls share same native function codes due to C++ compiler optimization
    // despite having different semantics in managed context, hook il2cpp_resolve_icall to return
    // a "copy" trampoline function for patching
    private static nint ResolveICall(string name)
    {
        // all internal call names are stored without parameter type names,
        // so it's safe to just strip it, and it's simpier for indexing
        var argIndex = name.IndexOf("(");
        if (argIndex != -1)
        {
            name = name[..argIndex];
        }

        // fast path for atomic read
        if (iCallTrampolines.TryGetValue(name, out var teampoline))
            return teampoline;

        var orig = OrigResolveICall(name);
        if (orig == 0)
            return 0;

        // lock for atomic read-or-set
        lock (iCallTrampolines)
        {
            if (iCallTrampolines.TryGetValue(name, out teampoline))
                return teampoline;

            try
            {
                teampoline = NativeUtils.CreateJmpTrampoline(orig);
            }
            catch (Exception e)
            {
                teampoline = orig;
                Patcher.Log.LogError(
                    $"ResolveICall: Failed to create trampoline function, fallback to original address {orig:x}, {e}"
                );
            }

            iCallTrampolines.TryAdd(name, teampoline);
        }

        // Patcher.Log.LogInfo($"Resolved ICall {name}, orig:{ptr:x}, trampoline:{res:x}");
        return teampoline;
    }
}
