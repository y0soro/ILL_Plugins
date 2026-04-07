#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace IL2CPP_ICall2Interop.Patcher.Interop;

// requires MonoMod Cecil DMD generator
internal static class MethodGenerator
{
    private static void LogStage(string stage)
    {
        var msg = $"stage: {stage}";
        Console.Error.WriteLine(msg);
        Patcher.Log.LogDebug(msg);
    }

    private static void EmitLogStage(this ILGenerator body, string stage)
    {
#if false
        body.Emit(OpCodes.Ldstr, stage);
        body.Emit(OpCodes.Call, HarmonyLib.AccessTools.Method(typeof(MethodGenerator), "LogStage"));
#endif
    }

    public static DynamicMethodDefinition GenManagedInteropToICallMethod(
        MethodInfo referenceInterop,
        nint iCallPtr,
        bool indirectLoadICallPtr,
        RuntimeReferences imports
    )
    {
        var interopDeclaringType = referenceInterop.DeclaringType!;
        var interopReturnType = referenceInterop.ReturnType;
        var nativeReturnType = interopReturnType.NativeType();

        var interopParams = referenceInterop.GetParameters();

        List<Type> nativeParams = [];
        if (!referenceInterop.IsStatic)
        {
            nativeParams.Add(interopDeclaringType.NativeType(true));
        }
        foreach (var param in interopParams)
        {
            // Patcher.Log.LogInfo(
            //     $"param {nativeParams.Count}: {param.ParameterType} -> {param.ParameterType.NativeType()}"
            // );
            nativeParams.Add(param.ParameterType.NativeType());
        }

        // Patcher.Log.LogInfo($"return: {interopReturnType} -> {nativeReturnType}");

        Type[] interopHookParams = referenceInterop.IsStatic
            ? [.. interopParams.Select(i => i.ParameterType)]
            : [referenceInterop.GetThisParamType(), .. interopParams.Select(i => i.ParameterType)];

        var dynamicMethod = new DynamicMethodDefinition(
            "(interop -> icall) " + interopDeclaringType.FullName + "::" + referenceInterop.Name,
            interopReturnType,
            interopHookParams
        );

        var body = dynamicMethod.GetILGenerator();

        var argOffset = 0;

        body.EmitLogStage(
            "interop: start " + interopDeclaringType.FullName + "::" + referenceInterop.Name
        );

        if (!referenceInterop.IsStatic)
        {
            body.EmitLogStage("interop: this");

            body.Emit(OpCodes.Ldarg_0);
            body.EmitConvertInteropToNative(
                interopDeclaringType,
                false,
                true,
                imports,
                out var refNativeVar
            );
            if (refNativeVar != null)
                throw new ArgumentException(
                    "Method instance should not need post-reference update"
                );

            argOffset = 1;
        }

        List<(int, Type, LocalBuilder)> refNativeVarList = [];

        for (var i = 0; i < interopParams.Length; i++)
        {
            var param = interopParams[i];
            var interopType = param.ParameterType;
            var isOutRef = interopType.IsByRef && param.IsOut;

            var argIndex = argOffset + i;

            body.EmitLogStage($"interop: arg {argIndex} {interopType.FullName} out:{isOutRef}");

            body.Emit(OpCodes.Ldarg, argIndex);

            body.EmitConvertInteropToNative(
                interopType,
                isOutRef,
                false,
                imports,
                out var refNativeVar
            );
            if (refNativeVar != null)
            {
                refNativeVarList.Add((argIndex, interopType, refNativeVar));
            }
        }

        body.EmitLogStage($"interop: calli native ptr");

        body.Emit(OpCodes.Ldc_I8, iCallPtr);
        body.Emit(OpCodes.Conv_I);

        if (indirectLoadICallPtr)
        {
            body.Emit(OpCodes.Ldind_I8);
            body.Emit(OpCodes.Conv_I);
        }

        body.EmitCalliImpl(CallingConvention.Cdecl, nativeReturnType, nativeParams);

        body.EmitConvertNativeToInterop(
            interopReturnType,
            false,
            false,
            imports,
            out var refInteropVar
        );
        if (refInteropVar != null)
            throw new ArgumentException(
                "Method return value should not need post-reference update"
            );

        foreach (var (argIndex, interopType, refNativeVar) in refNativeVarList)
        {
            body.EmitLogStage($"interop: update arg {argIndex}");

            body.Emit(OpCodes.Ldarg, argIndex);

            body.EmitUpdateInteropRef(interopType, imports, refNativeVar);
        }

        body.EmitLogStage($"interop: return");

        body.Emit(OpCodes.Ret);

        return dynamicMethod;
    }

    public static DynamicMethodDefinition GenNativeICallToInteropMethod(
        MethodInfo referenceInterop,
        MethodInfo icallInterop,
        RuntimeReferences imports
    )
    {
        var interopDeclaringType = referenceInterop.DeclaringType!;
        var interopReturnType = referenceInterop.ReturnType;
        var nativeReturnType = interopReturnType.NativeType();

        referenceInterop.GetThisParamType();

        var interopParams = referenceInterop.GetParameters();

        List<Type> nativeParams = [];
        if (!referenceInterop.IsStatic)
        {
            nativeParams.Add(interopDeclaringType.NativeType(true));
        }
        foreach (var param in interopParams)
        {
            nativeParams.Add(param.ParameterType.NativeType());
        }

        var dynamicMethod = new DynamicMethodDefinition(
            "(icall -> interop) " + interopDeclaringType.FullName + "::" + referenceInterop.Name,
            nativeReturnType,
            [.. nativeParams]
        );

        var body = dynamicMethod.GetILGenerator();

        var argOffset = 0;
        List<(int, Type, LocalBuilder)> refInteropVarList = [];

        var state = body.EmitBeginFilterTry();

        body.EmitLogStage(
            "icall: start " + interopDeclaringType.FullName + "::" + referenceInterop.Name
        );

        if (!referenceInterop.IsStatic)
        {
            body.EmitLogStage($"icall: this");

            body.Emit(OpCodes.Ldarg_0);

            body.EmitConvertNativeToInterop(
                interopDeclaringType,
                false,
                true,
                imports,
                out var refInteropVar
            );
            if (refInteropVar != null)
            {
                refInteropVarList.Add((0, interopDeclaringType, refInteropVar));
            }

            argOffset = 1;
        }

        for (var i = 0; i < interopParams.Length; i++)
        {
            var param = interopParams[i];
            var interopType = param.ParameterType;
            var isOutRef = interopType.IsByRef && param.IsOut;

            var argIndex = argOffset + i;

            body.EmitLogStage($"icall: arg {argIndex} {interopType.FullName} out:{isOutRef}");

            // Patcher.Log.LogInfo($"arg {i}: {param.ParameterType} {param.ParameterType.NativeType()}");

            body.Emit(OpCodes.Ldarg, argIndex);

            body.EmitConvertNativeToInterop(
                interopType,
                isOutRef,
                false,
                imports,
                out var refInteropVar
            );
            if (refInteropVar != null)
            {
                refInteropVarList.Add((argIndex, interopType, refInteropVar));
            }
        }

        body.EmitLogStage("icall: call interop method");

        body.Emit(OpCodes.Call, icallInterop);

        body.EmitConvertInteropToNative(
            interopReturnType,
            false,
            false,
            imports,
            out var refNativeVar
        );
        if (refNativeVar != null)
            throw new ArgumentException(
                "Method return value should not need post-reference update"
            );

        LocalBuilder? returnVar = null;
        if (nativeReturnType != typeof(void))
        {
            returnVar = body.DeclareLocal(nativeReturnType);
            body.Emit(OpCodes.Stloc, returnVar);
            body.Emit(OpCodes.Ldloc, returnVar);
        }

        foreach (var (argIndex, interopType, refInteropVar) in refInteropVarList)
        {
            body.EmitLogStage($"icall: update arg {argIndex}");

            body.Emit(OpCodes.Ldarg, argIndex);

            var isThisParam = !referenceInterop.IsStatic && argIndex == 0;
            body.EmitUpdateNativeRef(interopType, isThisParam, imports, refInteropVar);
        }

        body.EmitFilterRaiseSEHException(state, imports);

        body.EmitLogStage("icall: return");

        if (returnVar != null)
        {
            body.Emit(OpCodes.Ldloc, returnVar);
        }

        body.Emit(OpCodes.Ret);

        return dynamicMethod;
    }

    public static DynamicMethodDefinition GenStaticMethodWrapper(MethodInfo innerMethod)
    {
        if (!innerMethod.IsStatic)
            throw new ArgumentException();

        Type[] parameterTypes = [.. innerMethod.GetParameters().Select(i => i.ParameterType)];

        var dynamicMethod = new DynamicMethodDefinition(
            innerMethod.Name + " (Indirect)",
            innerMethod.ReturnType,
            parameterTypes
        );

        var body = dynamicMethod.GetILGenerator();

        for (int i = 0; i < parameterTypes.Length; i++)
        {
            body.Emit(OpCodes.Ldarg, i);
        }

        body.Emit(OpCodes.Call, innerMethod);
        body.Emit(OpCodes.Ret);

        return dynamicMethod;
    }
}
