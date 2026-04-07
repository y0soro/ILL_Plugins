using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using IL2CPP_ICall2Interop.Patcher.Interop;
using Il2CppInterop.Runtime;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace IL2CPP_ICall2Interop.Patcher;

internal class HarmonyICallPatcher : MethodPatcher
{
    private new readonly MethodInfo Original;
    private readonly nint originalICallPtr;
    private readonly RuntimeReferences imports = new();
    private readonly MethodInfo iCallToInteropHook;
    private readonly Type iCallToInteropHookDelegateType;
    private readonly Delegate iCallToInteropHookDelegate;

    // XXX: write and read of this buffer might not be atomic(i.e. multithread-safe)
    private readonly nint trampolinePtrBuffer = AllocPtrBuffer();
    private MethodInfo copiedOriginal = null;

    private Detour interopDetour = null;
    private INativeDetour nativeDetour = null;

    public HarmonyICallPatcher(MethodInfo original, nint originalICallPtr)
        : base(original)
    {
        Original = original;
        this.originalICallPtr = originalICallPtr;
        SetTrampolinePtr(originalICallPtr);

        iCallToInteropHook = MethodGenerator
            .GenNativeICallToInteropMethod(Original, Original, imports)
            .GenerateCecil();
        iCallToInteropHookDelegateType = DelegateTypeFactory.instance.CreateDelegateType(
            iCallToInteropHook,
            CallingConvention.Cdecl
        );
        iCallToInteropHookDelegate = iCallToInteropHook.CreateDelegate(
            iCallToInteropHookDelegateType
        );
    }

    public override DynamicMethodDefinition CopyOriginal()
    {
        copiedOriginal ??= MethodGenerator
            .GenManagedInteropToICallMethod(Original, trampolinePtrBuffer, true, imports)
            .GenerateCecil();

        // Harmony does not support generate reverse patch with calli call site,
        // so generate a method instance and wrap it with call
        return MethodGenerator.GenStaticMethodWrapper(copiedOriginal);
    }

    public override MethodBase DetourTo(MethodBase replacement)
    {
        try
        {
            return DetourToInternal();
        }
        catch
        {
            DisposeDetours();
            throw;
        }
    }

    private void DisposeDetours()
    {
        interopDetour?.Dispose();
        interopDetour = null;

        if (nativeDetour != null)
        {
            SetTrampolinePtr(originalICallPtr);
            nativeDetour.Dispose();
            nativeDetour = null;
        }
    }

    private MethodBase DetourToInternal()
    {
        DisposeDetours();

        nativeDetour = INativeDetour.Create(originalICallPtr, iCallToInteropHookDelegate);
        nativeDetour.Apply();

        SetTrampolinePtr(nativeDetour.TrampolinePtr);

        var interopToTrampolineICall = MethodGenerator.GenManagedInteropToICallMethod(
            Original,
            nativeDetour.TrampolinePtr,
            false,
            imports
        );
        // apply Harmony patch to replacement definition
        HarmonyManipulator.Manipulate(Original, new ILContext(interopToTrampolineICall.Definition));

        var interopToTrampolineICallMethod = interopToTrampolineICall.GenerateCecil();

        // detour the original managed interop method
        interopDetour = new Detour(Original, interopToTrampolineICallMethod);
        interopDetour.Apply();

        return interopToTrampolineICallMethod;
    }

    public override DynamicMethodDefinition PrepareOriginal()
    {
        return null;
    }

    private unsafe void SetTrampolinePtr(nint ptr)
    {
        Unsafe.AsRef<ulong>((void*)trampolinePtrBuffer) = (ulong)ptr;
    }

    private static unsafe nint AllocPtrBuffer()
    {
        return (nint)NativeMemory.AlignedAlloc(8, 8);
    }

    public static void TryResolve(object sender, PatchManager.PatcherResolverEventArgs args)
    {
        if (args.Original is not MethodInfo method)
            return;

        var iCallName = method.DeclaringType!.FullName + "::" + method.Name;

        var iCallPtr = IL2CPP.il2cpp_resolve_icall(iCallName);
        // this is not an internal call
        if (iCallPtr == (nint)0)
            return;

        if (Patcher.ICallDb.TryGetDescriptor(iCallName, out var descriptor))
        {
            var iCallMethod = ICallDatabase.ResolveMethodByDescriptor(descriptor);
            if (iCallMethod == null || iCallMethod != method)
                return;
        }
        else
        {
            var matchedName = false;
            foreach (var m in AccessTools.GetDeclaredMethods(method.DeclaringType))
            {
                if (!m.HasMethodBody() || m.Name != method.Name)
                    continue;

                if (matchedName)
                {
                    // registered internal call method name should be unique,
                    // but if there are overloads we cannot determine without searching original unity libs,
                    // such case should be rare so just skip
                    Patcher.Log.LogError(
                        $"Found ambiguous internal call interop methods with same name {iCallName}, skip hooking."
                    );
                    return;
                }

                matchedName = true;
            }
        }

        try
        {
            List<Type> checkTypes =
            [
                method.ReturnType,
                .. method.GetParameters().Select(i => i.ParameterType),
            ];
            if (!method.IsStatic)
                checkTypes.Add(method.DeclaringType);

            foreach (var checkType in checkTypes)
            {
                checkType.NativeType();
            }
        }
        catch (Exception e)
        {
            Patcher.Log.LogError(
                $"{method.DeclaringType.FullName}::{method.Name} has unsupported parameter type, {e.Message}"
            );
            return;
        }

        Patcher.Log.LogDebug($"Resolve {method.DeclaringType.FullName}::{method.Name}");

        args.MethodPatcher = new HarmonyICallPatcher(method, iCallPtr);
    }
}
