using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using IL2CPP_ICall2Interop.Patcher.Interop;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IL2CPP_ICall2Interop.Patcher;

public class TestInterop : BasePatcher
{
    private static readonly Harmony harmony = new(MyPluginInfo.PLUGIN_GUID + "Test");

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetMainWindowDisplayInfo_Injected_Native(nint ret);

    private delegate void GetMainWindowDisplayInfo_Injected(out UnityEngine.DisplayInfo ret);

    internal static new ManualLogSource Log;

    internal static string DumpDir = null;

    public override void Finalizer()
    {
        Log = base.Log;
        harmony.PatchAll(typeof(TestHooks));
    }

    private static class TestHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(IL2CPPChainloader), nameof(IL2CPPChainloader.Initialize))]
        private static void Initialize(IL2CPPChainloader __instance)
        {
            // hack to run at plugin loading stage
            __instance.Finished += TestInteropGen;
        }

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(Screen), nameof(Screen.GetMainWindowDisplayInfo_Injected))]
        private static void GetMainWindowDisplayInfo_Injected_Orig(ref DisplayInfo ret)
        {
            throw new InvalidOperationException();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Screen), nameof(Screen.GetMainWindowDisplayInfo_Injected))]
        private static void GetMainWindowDisplayInfo_Injected(ref DisplayInfo ret)
        {
            ret = null;
            GetMainWindowDisplayInfo_Injected_Orig(ref ret);
            Log.LogInfo($"hooked displayinfo {ret.width}x{ret.height}");

            // throw new ArgumentException("test managed");
            // var ex = new Il2CppSystem.Exception("test raise");
            // il2cpp_raise_exception(ex.Pointer);
        }
    }

    private static unsafe void TestInteropGen()
    {
        var method = AccessTools.Method(
            typeof(UnityEngine.Screen),
            nameof(UnityEngine.Screen.GetMainWindowDisplayInfo_Injected)
        );
        var icallPtr = IL2CPP.il2cpp_resolve_icall(
            "UnityEngine.Screen::GetMainWindowDisplayInfo_Injected"
        );

        var imports = new RuntimeReferences();
        var dmd = MethodGenerator.GenManagedInteropToICallMethod(method, icallPtr, false, imports);
        var dm = dmd.GenerateCecil();

        var nativeDmd = MethodGenerator.GenNativeICallToInteropMethod(method, method, imports);

        var nativeDm = nativeDmd.GenerateCecil();

        Log.LogInfo($"icall: {icallPtr} desc: {dm.FullDescription()}");

        var icall = (delegate* unmanaged[Cdecl]<ulong, void>)icallPtr;

        var info = new DisplayInfo { height = 16 };

        var infoValueRef = IL2CPP.il2cpp_object_unbox(IL2CPP.Il2CppObjectBaseToPtrNotNull(info));

        // icall((ulong)infoValueRef);

        var interopDelegate = dm.CreateDelegate<GetMainWindowDisplayInfo_Injected>();

        var nativeDelegate = nativeDm.CreateDelegate<GetMainWindowDisplayInfo_Injected_Native>();

        var hook = new MonoMod.RuntimeDetour.Hook(method, dm);

        try
        {
            // interopDelegate(out info);
            nativeDelegate(infoValueRef);
            // UnityEngine.Screen.GetMainWindowDisplayInfo_Injected(out info);
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }

        Log.LogInfo($"displayinfo: height {info.height}");
    }
}
