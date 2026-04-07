using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace IL2CPP_ICall2Interop.Patcher.Interop;

internal class RuntimeReferences
{
    public MethodInfo IL2CPP_Il2CppObjectBaseToPtrNotNull = AccessTools.Method(
        typeof(IL2CPP),
        nameof(IL2CPP.Il2CppObjectBaseToPtrNotNull)
    );
    public MethodInfo IL2CPP_Il2CppObjectBaseToPtr = AccessTools.Method(
        typeof(IL2CPP),
        nameof(IL2CPP.Il2CppObjectBaseToPtr)
    );
    public MethodInfo IL2CPP_ManagedStringToIl2Cpp = AccessTools.Method(
        typeof(IL2CPP),
        nameof(IL2CPP.ManagedStringToIl2Cpp)
    );
    public MethodInfo IL2CPP_Il2CppStringToManaged = AccessTools.Method(
        typeof(IL2CPP),
        nameof(IL2CPP.Il2CppStringToManaged)
    );

    // public MethodInfo IL2CPP_PointerToValueGeneric = null;
    public MethodInfo Il2CppObjectPool_Get = AccessTools.Method(
        typeof(Il2CppObjectPool),
        nameof(Il2CppObjectPool.Get)
    );

    // public MethodInfo IL2CPP_il2cpp_object_get_class = null;
    // public MethodInfo IL2CPP_il2cpp_class_is_valuetype = null;
    public MethodInfo IL2CPP_il2cpp_object_unbox = AccessTools.Method(
        typeof(IL2CPP),
        nameof(IL2CPP.il2cpp_object_unbox)
    );
    public MethodInfo IL2CPP_il2cpp_value_box = AccessTools.Method(
        typeof(IL2CPP),
        nameof(IL2CPP.il2cpp_value_box)
    );

    // public MethodInfo TypeGetTypeFromHandle = null;
    // public MethodInfo TypeGetIsValueType = null;

    // public Type Il2CppClassPointerStore = typeof(Il2CppClassPointerStore<>);
    // public Type Il2CppArrayBase = typeof(Il2CppArrayBase<>);
    //
    //

    public MethodInfo Method_Il2cppRaiseMappedManagedException = AccessTools.Method(
        typeof(RuntimeReferences),
        nameof(Il2cppRaiseMappedManagedException)
    );

    [DoesNotReturn]
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    public static extern void il2cpp_raise_exception(nint exception);

    [DoesNotReturn]
    public static void Il2cppRaiseMappedManagedException(Exception e)
    {
        var il2cppEx = new Il2CppSystem.Exception(
            $"(il2cpp -> managed) {e.GetType().FullName}: {e.Message}"
                + "\n--- BEGIN MANAGED EXCEPTION ---\n"
                + e.ToString()
                + "\n---  END MANAGED EXCEPTION  ---\n"
        );

        il2cpp_raise_exception(il2cppEx.Pointer);
    }
}
