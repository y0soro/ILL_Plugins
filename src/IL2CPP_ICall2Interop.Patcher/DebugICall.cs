using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace IL2CPP_ICall2Interop.Patcher;

internal static class DebugICall
{
    public static void HookAllICalls()
    {
        var preloadInterop = AccessTools.Method(
            "BepInEx.Unity.IL2CPP.Il2CppInteropManager:PreloadInteropAssemblies"
        );
        if (preloadInterop == null)
            return;

        preloadInterop.Invoke(null, []);

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        var cnt = 0;

        var start = 0;
        var end = int.MaxValue;

        string[] skipICalls = [];

        string[] debugICalls =
        [
            // "UnityEngine.Camera::CalculateFrustumCornersInternal_Injected",
            // "UnityEngine.Windows.WebCam.PhotoCaptureFrame",
            // "UnityEngine.Texture2D::GetRawTextureData"
            // "Unity.IO.LowLevel.Unsafe.AsyncReadManagerMetrics::GetSummaryOfMetricsWithFilters_Internal",
            // "UnityEngine.XR.XRDisplaySubsystem::Internal_TryGetRenderPass",
            // "UnityEngine.XR.XRMeshSubsystem",
            // "UnityEngine.Experimental.Rendering.GraphicsFormatUtility::",
            // "UnityEngine.Experimental.Rendering.GraphicsFormatUtility::GetDepthStencilFormatFromBitsLegacy_Native",
            // "UnityEngine.Experimental.Rendering.GraphicsFormatUtility::IsDepthStencilFormat",
        ];

        Dictionary<nint, List<string>> icallPtr2Name = [];
        Dictionary<string, nint> icallName2Ptr = [];

        foreach (var name in Patcher.ICallDb.ByMethodName.Keys)
        {
            if (string.IsNullOrEmpty(name) || icallName2Ptr.ContainsKey(name))
                continue;

            var ptr = IL2CPP.il2cpp_resolve_icall(name);

            icallName2Ptr[name] = ptr;

            if (!icallPtr2Name.TryGetValue(ptr, out var list))
            {
                icallPtr2Name[ptr] = list = [];
            }
            list.Add(name);
        }

        var dummyPrefix = AccessTools.Method(typeof(DebugICall), nameof(DummyHook));

        foreach (var (name, ptr) in icallName2Ptr)
        {
            if (cnt == end)
                break;
            if (cnt < start)
            {
                cnt++;
                continue;
            }

            var linePrefix = $"{cnt:D4}: {name} {ptr:x}";

            try
            {
                if (debugICalls.Length != 0 && !debugICalls.Any(name.StartsWith))
                    continue;

                if (icallPtr2Name[ptr].Count != 1)
                {
                    Console.Error.WriteLine(
                        $"{linePrefix} same ptr: {ptr:x}, {string.Join(", ", icallPtr2Name[ptr])}"
                    );
                    continue;
                }

                if (skipICalls.Any(name.StartsWith))
                    continue;

                // could return false if missing unity libs assembly for this internal call
                Patcher.ICallDb.TryGetMethodInfo(name, out var methodByDb);

                var methodByName = FindICallManagedMethodInfo(name);

                Console.Error.WriteLine(
                    $"{linePrefix} by_db:{methodByDb != null} by_name:{methodByName != null}"
                );

                var method = methodByDb ?? methodByName;
                if (method == null)
                    continue;

                harmony.Patch(method, prefix: new(dummyPrefix));
            }
            catch (Exception e)
            {
                if (e is TypeLoadException)
                {
                    Console.Error.WriteLine($"{linePrefix} broken type, {e.Message}");
                }
                else
                {
                    Console.Error.WriteLine($"{linePrefix} error: {e}");
                }
            }
            finally
            {
                cnt++;
            }
        }
    }

    private static void DummyHook() { }

    private static bool ParseICallName(
        string name,
        out string namespaze,
        out string className,
        out string methodName,
        out string[] args
    )
    {
        namespaze = null;
        className = null;
        methodName = null;
        args = null;

        var methodSepIdx = name.IndexOf("::");
        if (methodSepIdx < 0)
            return false;

        var classFullName = name[..methodSepIdx];
        var methodWithArgs = name[(methodSepIdx + 2)..];

        var argSepIdx = methodWithArgs.IndexOf('(');
        var matchArgs = argSepIdx >= 0;
        methodName = argSepIdx >= 0 ? methodWithArgs[..argSepIdx] : methodWithArgs;

        if (matchArgs)
        {
            var argStr = methodWithArgs[(argSepIdx + 1)..];
            var endSepIdx = argStr.LastIndexOf(')');
            if (endSepIdx < 0)
                return false;
            args = [.. argStr[..endSepIdx].Split(',', StringSplitOptions.RemoveEmptyEntries)];
        }
        else
        {
            args = null;
        }

        var lastDotIdx = classFullName.LastIndexOf('.');
        if (lastDotIdx < 0)
            return false;

        namespaze = classFullName[..lastDotIdx];
        className = classFullName[(lastDotIdx + 1)..];

        return true;
    }

    private static MethodInfo FindICallManagedMethodInfo(string name)
    {
        if (!ParseICallName(name, out var namespaze, out var className, out var methodName, out _))
            return null;

        var fullName = $"{namespaze}.{className}".Replace('/', '+');

        Type currType = Type.GetType(fullName);
        if (currType == null)
            return null;

        var methods = AccessTools
            .GetDeclaredMethods(currType)
            .Where(m => m.Name == methodName)
            .ToArray();

        if (methods.Length == 0)
            return null;
        else if (methods.Length == 1)
            return methods[0];

        Console.Error.WriteLine(
            $"failed to find exact method: {fullName}::{methodName}, got {methods.Length}"
        );

        return null;
    }
}
