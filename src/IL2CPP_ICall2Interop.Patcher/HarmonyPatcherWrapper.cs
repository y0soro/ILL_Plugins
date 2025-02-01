using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using MonoMod.Utils;

namespace IL2CPP_ICall2Interop.Patcher;

internal class HarmonyPatcherWrapper : MethodPatcher
{
    static readonly Dictionary<MethodBase, GCHandle> iCallProxyStore = [];

    private readonly MethodPatcher prevPatcher;

    public new MethodBase Original
    {
        get => prevPatcher.Original;
    }

    private HarmonyPatcherWrapper(MethodBase original, MethodPatcher prevPatcher)
        : base(original)
    {
        this.prevPatcher = prevPatcher;
    }

    public override DynamicMethodDefinition CopyOriginal()
    {
        return prevPatcher.CopyOriginal();
    }

    public override DynamicMethodDefinition PrepareOriginal()
    {
        return prevPatcher.PrepareOriginal();
    }

    public override MethodBase DetourTo(MethodBase replacement)
    {
        var patchInfo = Original.GetPatchInfo();

        static void AddPatch(ref Patch[] patches, MethodInfo patchMethod)
        {
            if (patches.All((patch) => patch.PatchMethod != patchMethod))
            {
                patches =
                [
                    new(new(patchMethod, priority: Priority.First), 0, MyPluginInfo.PLUGIN_GUID),
                    .. patches,
                ];
            }
        }

        AddPatch(
            ref patchInfo.prefixes,
            AccessTools.Method(typeof(HarmonyPatcherWrapper), nameof(GenPrefix))
        );

        AddPatch(
            ref patchInfo.finalizers,
            AccessTools.Method(typeof(HarmonyPatcherWrapper), nameof(GenFinalizer))
        );

        var handle = GetOrCreateICallProxyHandle(Original);
        var iCallProxy = (ICallProxy)handle.Target;

        iCallProxy.SetManagedPatched();

        if (iCallProxy.OriginalICall != 0)
            Patcher.Log.LogDebug(
                $"Patched Managed ICall 0x{iCallProxy.OriginalICall:x} -> 0x{iCallProxy.ResolvedICall:x}: {Original.DeclaringType.FullName}::{Original.Name}"
            );
        return prevPatcher.DetourTo(replacement);
    }

    private static DynamicMethod GenPrefix(MethodBase original)
    {
        var handle = GetOrCreateICallProxyHandle(original);
        nint handlePtr = GCHandle.ToIntPtr(handle);

        var dm = new DynamicMethod("ICallProxyEnterManaged", typeof(void), []);

        var il = dm.GetILGenerator();

        il.Emit(OpCodes.Ldc_I8, handlePtr);
        il.Emit(OpCodes.Conv_I);
        il.Emit(
            OpCodes.Call,
            AccessTools.Method(typeof(ICallProxy), nameof(ICallProxy.EnterManagedFromHandle))
        );
        il.Emit(OpCodes.Ret);

        return dm;
    }

    private static DynamicMethod GenFinalizer(MethodBase original)
    {
        var handle = GetOrCreateICallProxyHandle(original);
        nint handlePtr = GCHandle.ToIntPtr(handle);

        var dm = new DynamicMethod("ICallProxyExitManaged", typeof(void), []);

        var il = dm.GetILGenerator();

        il.Emit(OpCodes.Ldc_I8, handlePtr);
        il.Emit(OpCodes.Conv_I);
        il.Emit(
            OpCodes.Call,
            AccessTools.Method(typeof(ICallProxy), nameof(ICallProxy.ExitManagedFromHandle))
        );
        il.Emit(OpCodes.Ret);

        return dm;
    }

    public static GCHandle GetOrCreateICallProxyHandle(MethodBase original)
    {
        lock (iCallProxyStore)
        {
            if (!iCallProxyStore.TryGetValue(original, out var iCallProxyHandle))
            {
                var iCallProxy = new ICallProxy(original);
                iCallProxyHandle = GCHandle.Alloc(iCallProxy);
                iCallProxyStore[original] = iCallProxyHandle;
            }

            return iCallProxyHandle;
        }
    }

    public static void WrapPatcher(object sender, PatchManager.PatcherResolverEventArgs args)
    {
        var method = args.Original;

        var patcher = args.MethodPatcher;
        if (patcher == null)
            return;

        Patcher.Log.LogDebug(
            $"TryResolve {method.DeclaringType.FullName}::{method.Name}, previous patcher {patcher}"
        );

        args.MethodPatcher = new HarmonyPatcherWrapper(patcher.Original, patcher);
    }
}
