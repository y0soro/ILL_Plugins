using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using IL2CPP_ICall2Interop.Patcher;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime;

internal static class ResolveICallHook
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ICallResolveDelegate([MarshalAs(UnmanagedType.LPStr)] string name);

    private static INativeDetour detour = null;

    private static ICallResolveDelegate OrigResolveICall;

    private static readonly ConcurrentDictionary<string, nint> iCallTrampolines = [];

    // pre-replace internal calls so even if hook failed to apply onto the InternalCalls::Resolve
    // implementation or the function has been inlined, native codes can still resolve to out trampoline
    public static void PreReplaceICalls(ICallDatabase iCallDb)
    {
        if (detour != null)
            throw new InvalidOperationException("required to run before Install() native hook");

        foreach (var name in iCallDb.ByMethodName.Keys)
        {
            if (iCallTrampolines.ContainsKey(name))
                continue;

            nint orig = IL2CPP.il2cpp_resolve_icall(name);
            if (orig == 0)
                continue;

            nint trampoline;
            try
            {
                trampoline = NativeUtils.CreateJmpTrampoline(orig);
            }
            catch
            {
                continue;
            }

            il2cpp_add_internal_call(name, trampoline);
            iCallTrampolines[name] = trampoline;
        }
    }

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

        // detour the internal InternalCalls::Resolve implementation if possible
        var jmpTargets = XrefScannerLowLevel.JumpTargets(il2cpp_resolve_icall).ToArray();
        if (jmpTargets.Length == 1)
            il2cpp_resolve_icall = jmpTargets[0];

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
        if (iCallTrampolines.TryGetValue(name, out var trampoline))
            return trampoline;

        var orig = OrigResolveICall(name);
        if (orig == 0)
            return 0;

        // lock for atomic read-or-set
        lock (iCallTrampolines)
        {
            if (iCallTrampolines.TryGetValue(name, out trampoline))
                return trampoline;

            try
            {
                trampoline = NativeUtils.CreateJmpTrampoline(orig);

                if (!Patcher.DisableReplacing)
                {
                    il2cpp_add_internal_call(name, trampoline);
                }
            }
            catch (Exception e)
            {
                trampoline = orig;
                Patcher.Log.LogError(
                    $"ResolveICall: Failed to create trampoline function, fallback to original address {orig:x}, {e}"
                );
            }

            iCallTrampolines[name] = trampoline;
        }

        // Patcher.Log.LogInfo($"Resolved ICall {name}, orig:{orig:x}, trampoline:{trampoline:x}");
        return trampoline;
    }

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void il2cpp_add_internal_call(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        nint method
    );
}
