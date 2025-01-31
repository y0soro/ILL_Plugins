using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Il2CppInterop.Common.XrefScans;

namespace IL2CPP_ICall2Interop.Patcher;

[PatcherPluginInfo(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Patcher : BasePatcher
{
    internal static new ManualLogSource Log;
    internal static IntPtr Il2CppHandle = NativeLibrary.Load(
        "GameAssembly",
        typeof(Patcher).Assembly,
        null
    );

    internal static ConcurrentDictionary<
        MethodInfo,
        (ICallProxy, GCHandle, Delegate)
    > resolvedICallMethod = [];
    internal static ConcurrentDictionary<string, MethodInfo> resolvedICallName = [];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ICallResolveDelegate([MarshalAs(UnmanagedType.LPStr)] string name);

    static readonly ICallResolveDelegate hookICallResolve = ICallResolveCollectMethod;
    static ICallResolveDelegate origICallResolve = null;

    public override unsafe void Finalizer()
    {
        Log = base.Log;

        NativeLibrary.TryGetExport(
            Il2CppHandle,
            "il2cpp_resolve_icall",
            out var il2cpp_resolve_icall
        );
        var internalCallsResolve = XrefScannerLowLevel.JumpTargets(il2cpp_resolve_icall).Single();

        Log.LogDebug(
            $"Patching il2cpp_resolve_icall:0x{il2cpp_resolve_icall:x} internalCallsResolve:0x{internalCallsResolve:x}"
        );

        INativeDetour.CreateAndApply(internalCallsResolve, hookICallResolve, out origICallResolve);

        PatchManager.ResolvePatcher += HarmonyPatcherWrapper.WrapPatcher;
    }

    private static unsafe nint ICallResolveCollectMethod(string name)
    {
        MethodInfo methodInfo;
        GCHandle handle;
        try
        {
            if (!resolvedICallName.TryGetValue(name, out methodInfo))
            {
                methodInfo = FindICallManagedMethodInfo(name);
                resolvedICallName[name] = methodInfo;
            }

            if (methodInfo == null)
                return origICallResolve(name);

            handle = HarmonyPatcherWrapper.GetOrCreateICallProxyHandle(methodInfo);
        }
        catch (Exception e)
        {
            Log.LogError(e);
            return origICallResolve(name);
        }

        var iCallProxy = (ICallProxy)handle.Target;

        if (iCallProxy.ResolvedICall != 0)
            return iCallProxy.ResolvedICall;

        var originalICall = origICallResolve(name);
        if (originalICall == 0)
            return 0;

        var args = string.Join(", ", methodInfo.GetParameters().Select(i => i.ParameterType.Name));

        Log.LogDebug(
            $"ICallResolve: {name} 0x{originalICall:x}: {methodInfo.ReturnType.Name} {methodInfo.DeclaringType.FullName}::{methodInfo.Name}({args})"
        );

        try
        {
            var nativeToManagedMethod = InteropUtils.GenerateNativeToManagedTrampoline(
                GCHandle.ToIntPtr(handle),
                methodInfo
            );

            var unmanagedDelegateType = DelegateTypeFactory.instance.CreateDelegateType(
                nativeToManagedMethod,
                CallingConvention.Cdecl
            );

            if (unmanagedDelegateType == null)
            {
                Log.LogDebug($"failed to create delegate type for proxy method");
                iCallProxy.ResolvedICall = originalICall;
                iCallProxy.OriginalICall = 0;

                return originalICall;
            }

            var unmanagedDelegate = nativeToManagedMethod.CreateDelegate(unmanagedDelegateType);

#if false
            // The original internal call might not have enough function body size for native patching,
            // so create a trampoline that jumps to the original internal call with reserved function body bytes for patching.
            var jmpOriginalTrampoline = Utils.CreateJmpTrampoline(originalICall);

            var detour = INativeDetour.Create(jmpOriginalTrampoline, unmanagedDelegate);
            detour.Apply();

            iCallProxy.ResolvedICall = jmpOriginalTrampoline;
            iCallProxy.OriginalICall = detour.TrampolinePtr;
#else
            // Or just return the pointer of hooking method.
            //
            // An indirect JMP trampoline is needed to avoid delegate type casting error when
            // using Marshal.GetDelegateForFunctionPointer on pointer returned here,
            // as .NET runtime appears to be linking delegate type with marshaled pointer
            iCallProxy.ResolvedICall = Utils.CreateJmpTrampoline(
                Marshal.GetFunctionPointerForDelegate(unmanagedDelegate)
            );
            iCallProxy.OriginalICall = originalICall;
#endif

            resolvedICallMethod[methodInfo] = (iCallProxy, handle, unmanagedDelegate);
        }
        catch (Exception e)
        {
            Log.LogError(e);

            iCallProxy.ResolvedICall = originalICall;
            iCallProxy.OriginalICall = 0;

            return originalICall;
        }

        if (iCallProxy.ManagedPatched)
            Log.LogDebug(
                $"Patched ICall 0x{originalICall:x} -> 0x{iCallProxy.ResolvedICall:x}: {name}"
            );

        return iCallProxy.ResolvedICall;
    }

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

    private static string ConvertInteropType(string typeName)
    {
        var token = "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1[[";

        if (typeName.StartsWith(token))
        {
            typeName = typeName[token.Length..];
            var idx = typeName.IndexOf(',');
            if (idx < 0)
            {
                throw new Exception($"Unhandled type name {typeName}");
            }

            typeName = $"{typeName[..idx]}[]";
        }

        if (typeName.StartsWith("Il2Cpp"))
        {
            typeName = typeName[6..];
        }

        typeName = typeName.Replace('+', '/');
        return typeName;
    }

    private static bool IsInteropICall(MethodInfo method)
    {
        var methodBody = method.GetMethodBody();
        if (methodBody == null)
            return false;

        var argNames = string.Join(
            ", ",
            method.GetParameters().Select((param) => param.ParameterType.Name)
        );
        Log.LogDebug($"matching ICall {method.Name}({argNames})");

        var methodModule = method.DeclaringType.Assembly.Modules.Single();

        foreach (var (opCode, opArg) in MiniIlParser.Decode(methodBody.GetILAsByteArray()))
        {
            Log.LogDebug($"{opCode.Name}, 0x{opArg:x}");
            if (opCode != OpCodes.Ldsfld)
                continue;

            var fieldInfo = methodModule.ResolveField(
                (int)opArg,
                method.DeclaringType.GenericTypeArguments,
                method.GetGenericArguments()
            );
            if (fieldInfo == null || !fieldInfo.FieldType.IsSubclassOf(typeof(Delegate)))
                continue;

            if (fieldInfo.Name.StartsWith(method.Name))
                return true;
        }

        return false;
    }

    private static unsafe MethodInfo FindICallManagedMethodInfo(string name)
    {
        if (
            !ParseICallName(
                name,
                out var namespaze,
                out var className,
                out var methodName,
                out var args
            )
        )
        {
            return null;
        }
        if (args == null)
            return null;

        var fullName = $"{namespaze}.{className}";

        Type currType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            currType = assembly.GetType(fullName);
            if (currType != null)
                break;
        }
        if (currType == null)
        {
            Log.LogWarning($"internal call class {fullName} not found");
            return null;
        }

        var methods = currType.GetMethods();

        methods =
        [
            .. methods.Where(
                (method) =>
                    method.Name == methodName
                    && !method.IsAbstract
                    && !method.IsConstructor
                    && method.HasMethodBody()
                    && (args == null || method.GetParameters().Length == args.Length)
            ),
        ];

        if (methods.Length == 0)
            return null;
        else if (methods.Length == 1)
            return methods[0];

        if (args != null)
        {
            var exactMatch = methods.FirstOrDefault(
                (method) =>
                {
                    var parameters = method.GetParameters();
                    for (int i = 0; i < args.Length; i++)
                    {
                        var expectedArgType = args[i];
                        var argType = ConvertInteropType(parameters[i].ParameterType.FullName);

                        if (expectedArgType != argType)
                        {
                            // Log.LogWarning($"{expectedArgType} {argType}");
                            return false;
                        }
                    }
                    return true;
                },
                null
            );

            if (exactMatch != null)
                return exactMatch;
        }
        else
        {
            // unstripped Internal Calls, it has no parameter type signatures so we match generated ICall pattern instead

            methods = [.. methods.Where(IsInteropICall)];
            if (methods.Length > 1)
            {
                Log.LogWarning("matched multiple ICall");
            }
        }

        if (methods.Length == 1)
            return methods[0];

        Log.LogWarning(
            $"failed to find exact method: {fullName}::{methodName}, got {methods.Length}"
        );
        return null;
    }
}
