// Copyright: Il2CppInterop contributors
// SPDX-License-Identifier: LGPL-3.0-only
// modified from Il2CppInterop.HarmonySupport/Il2CppDetourMethodPatcher.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;

namespace IL2CPP_ICall2Interop.Patcher;

internal static partial class InteropUtils
{
    private static readonly MethodInfo IL2CPPToManagedStringMethodInfo =
        AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.Il2CppStringToManaged))
        ?? throw new Exception("no ReportException");

    private static readonly MethodInfo ManagedToIL2CPPStringMethodInfo =
        AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.ManagedStringToIl2Cpp))
        ?? throw new Exception("no ManagedStringToIl2Cpp");

    private static readonly MethodInfo ObjectBaseToPtrMethodInfo =
        AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.Il2CppObjectBaseToPtr))
        ?? throw new Exception("no Il2CppObjectBaseToPtr");

    private static readonly MethodInfo ObjectBaseToPtrNotNullMethodInfo =
        AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.Il2CppObjectBaseToPtrNotNull))
        ?? throw new Exception("no Il2CppObjectBaseToPtrNotNull");

    private static readonly MethodInfo IL2CPPValueBoxMethodInfo = AccessTools.Method(
        typeof(IL2CPP),
        nameof(IL2CPP.il2cpp_value_box)
    );

    private static readonly MethodInfo ReportExceptionMethodInfo =
        AccessTools.Method(typeof(InteropUtils), nameof(ReportException))
        ?? throw new Exception("no ReportException");

    private static readonly MethodInfo ICallUsetargetManagedMethodInfoICallFromHandleMethodInfo =
        AccessTools.Method(typeof(ICallProxy), nameof(ICallProxy.UseOriginalICallFromHandle));

    // Map each value type to correctly sized store opcode to prevent memory overwrite
    // Special case: bool is byte in Il2Cpp
    private static readonly Dictionary<Type, OpCode> StIndOpcodes = new()
    {
        [typeof(byte)] = OpCodes.Stind_I1,
        [typeof(sbyte)] = OpCodes.Stind_I1,
        [typeof(bool)] = OpCodes.Stind_I1,
        [typeof(short)] = OpCodes.Stind_I2,
        [typeof(ushort)] = OpCodes.Stind_I2,
        [typeof(int)] = OpCodes.Stind_I4,
        [typeof(uint)] = OpCodes.Stind_I4,
        [typeof(long)] = OpCodes.Stind_I8,
        [typeof(ulong)] = OpCodes.Stind_I8,
        [typeof(float)] = OpCodes.Stind_R4,
        [typeof(double)] = OpCodes.Stind_R8,
    };

    // Tries to guess whether a function needs a return buffer for the return struct, in all cases except win64 it's undefined behaviour
    private static bool IsReturnBufferNeeded(int size)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170#return-values
            return size != 1 && size != 4 && size != 8;
        }

        if (Environment.Is64BitProcess)
        {
            // x64 gcc and clang seem to use a return buffer for everything above 16 bytes
            return size > 16;
        }

        // Looks like on x32 gcc and clang return buffer is always used
        return true;
    }

    public static DynamicMethod GenerateNativeToManagedTrampolineBak(
        nint iCallProxyGcHandle,
        MethodInfo managedMethod
    )
    {
        // managedParams are the interop types used on the managed side
        // unmanagedParams are IntPtr references that are used by IL2CPP compiled assembly
        var paramStartIndex = 0;

        var managedReturnType = AccessTools.GetReturnedType(managedMethod);
        var unmanagedReturnType = managedReturnType.NativeType();

        var returnSize = IntPtr.Size;

        var isReturnValueType = managedReturnType.IsSubclassOf(typeof(Il2CppSystem.ValueType));

        if (isReturnValueType)
        {
            nint nativeClassPtr = Il2CppClassPointerStore.GetNativeClassPointer(managedReturnType);
            if (nativeClassPtr == 0)
            {
                // assumes native register size, mostly for enum
                throw new Exception("unreachable");
            }
            else
            {
                uint align = 0;
                returnSize = IL2CPP.il2cpp_class_value_size(nativeClassPtr, ref align);
            }
        }

        var hasReturnBuffer = isReturnValueType && IsReturnBufferNeeded(returnSize);

        if (hasReturnBuffer)
        // C compilers seem to return large structs by allocating a return buffer on caller's side and passing it as the first parameter
        // TODO: Handle ARM
        // TODO: Check if this applies to values other than structs
        {
            Patcher.Log.LogInfo($"has return buffer {managedMethod.Name}");
            unmanagedReturnType = typeof(IntPtr);
            paramStartIndex++;
        }

        if (!managedMethod.IsStatic)
        {
            paramStartIndex++;
        }

        var managedParams = managedMethod.GetParameters().Select(x => x.ParameterType).ToArray();
        var unmanagedParams = new Type[managedParams.Length + paramStartIndex];

        if (hasReturnBuffer)
        // With GCC the return buffer seems to be the first param, same is likely with other compilers too
        {
            unmanagedParams[0] = typeof(IntPtr);
        }

        if (!managedMethod.IsStatic)
        {
            unmanagedParams[paramStartIndex - 1] = typeof(IntPtr);
        }

        Array.Copy(
            managedParams.Select(TrampolineHelpers.NativeType).ToArray(),
            0,
            unmanagedParams,
            paramStartIndex,
            managedParams.Length
        );

        var dynamicMethod = new DynamicMethod(
            "(internal call -> managed) " + managedMethod.Name,
            unmanagedReturnType,
            unmanagedParams
        );

        Patcher.Log.LogDebug(
            $"{managedMethod.Name} ret:{unmanagedReturnType.Name} params len:{unmanagedParams.Length}"
        );

        var il = dynamicMethod.GetILGenerator();

        for (int i = 0; i < unmanagedParams.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, i);
        }

        il.Emit(OpCodes.Ldc_I8, iCallProxyGcHandle);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Call, ICallUsetargetManagedMethodInfoICallFromHandleMethodInfo);

        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Tailcall);
        il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, unmanagedReturnType, unmanagedParams);

        il.Emit(OpCodes.Ret);

        return dynamicMethod;
    }

    public static DynamicMethod GenerateNativeToManagedTrampoline(
        nint iCallProxyGcHandle,
        MethodInfo managedMethod
    )
    {
        // managedParams are the interop types used on the managed side
        // unmanagedParams are IntPtr references that are used by IL2CPP compiled assembly
        var paramStartIndex = 0;

        var managedReturnType = AccessTools.GetReturnedType(managedMethod);
        var unmanagedReturnType = managedReturnType.NativeType();

        var returnSize = IntPtr.Size;

        var isReturnValueType = managedReturnType.IsSubclassOf(typeof(Il2CppSystem.ValueType));

        if (isReturnValueType)
        {
            nint nativeClassPtr = Il2CppClassPointerStore.GetNativeClassPointer(managedReturnType);
            if (nativeClassPtr == 0)
            {
                throw new Exception("no NativeClassPointer");
            }
            else
            {
                uint align = 0;
                returnSize = IL2CPP.il2cpp_class_value_size(nativeClassPtr, ref align);
            }
        }

        var hasReturnBuffer = isReturnValueType && IsReturnBufferNeeded(returnSize);

        if (hasReturnBuffer)
        // C compilers seem to return large structs by allocating a return buffer on caller's side and passing it as the first parameter
        // TODO: Handle ARM
        // TODO: Check if this applies to values other than structs
        {
            unmanagedReturnType = typeof(IntPtr);
            paramStartIndex++;
        }

        if (!managedMethod.IsStatic)
        {
            paramStartIndex++;
        }

        foreach (var p in managedMethod.GetParameters())
        {
            var ty = p.ParameterType;
            var hasOut = p.IsDefined(typeof(OutAttribute));

            if (hasOut)
            {
                Patcher.Log.LogDebug(
                    "skipping, parameter has OutAttribute or InAttribute, the generated interop method could be broken"
                );
                return null;
            }
        }

        var managedParams = managedMethod.GetParameters();
        var unmanagedParams = new Type[managedParams.Length + paramStartIndex];

        if (hasReturnBuffer)
        // With GCC the return buffer seems to be the first param, same is likely with other compilers too
        {
            unmanagedParams[0] = typeof(IntPtr);
        }

        if (!managedMethod.IsStatic)
        {
            unmanagedParams[paramStartIndex - 1] = typeof(IntPtr);
        }

        Array.Copy(
            managedParams.Select(x => TrampolineHelpers.NativeType(x.ParameterType)).ToArray(),
            0,
            unmanagedParams,
            paramStartIndex,
            managedParams.Length
        );

        var dynamicMethod = new DynamicMethod(
            "(internal call -> managed) " + managedMethod.Name,
            unmanagedReturnType,
            unmanagedParams
        );

        Patcher.Log.LogDebug(
            $"{managedMethod.Name} ret:{unmanagedReturnType.Name} params len:{unmanagedParams.Length}"
        );

        var il = dynamicMethod.GetILGenerator();

        il.Emit(OpCodes.Ldc_I8, iCallProxyGcHandle);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Call, ICallUsetargetManagedMethodInfoICallFromHandleMethodInfo);

        LocalBuilder usetargetManagedMethodInfo = il.DeclareLocal(typeof(nint));
        il.Emit(OpCodes.Stloc, usetargetManagedMethodInfo);
        il.Emit(OpCodes.Ldloc, usetargetManagedMethodInfo);

        var labelCallManaged = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, labelCallManaged);

        for (int i = 0; i < unmanagedParams.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, i);
        }

        il.Emit(OpCodes.Ldloc, usetargetManagedMethodInfo);
        il.Emit(OpCodes.Conv_I);

        il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, unmanagedReturnType, unmanagedParams);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(labelCallManaged);

        // il.BeginExceptionBlock();

        // Declare a list of variables to dereference back to the original pointers.
        // This is required due to the needed interop type conversions, so we can't directly pass some addresses as byref types
        var indirectVariables = new LocalBuilder[managedParams.Length];

        if (!managedMethod.IsStatic)
        {
            if (
                !EmitConvertArgumentToManaged(
                    il,
                    paramStartIndex - 1,
                    managedMethod.DeclaringType,
                    false,
                    out _
                )
            )
                return null;
        }

        for (var i = 0; i < managedParams.Length; ++i)
        {
            var p = managedParams[i];
            var hasOutAttr = p.IsDefined(typeof(OutAttribute));

            if (
                !EmitConvertArgumentToManaged(
                    il,
                    i + paramStartIndex,
                    p.ParameterType,
                    hasOutAttr,
                    out indirectVariables[i]
                )
            )
                return null;
        }

        // Run the managed method
        il.Emit(OpCodes.Call, managedMethod);

        // Store the managed return type temporarily (if there was one)
        LocalBuilder managedReturnVariable = null;
        if (managedReturnType != typeof(void))
        {
            managedReturnVariable = il.DeclareLocal(managedReturnType);
            il.Emit(OpCodes.Stloc, managedReturnVariable);
        }

        // Convert any managed byref values into their relevant IL2CPP types, and then store the values into their relevant dereferenced pointers
        for (var i = 0; i < managedParams.Length; ++i)
        {
            if (indirectVariables[i] == null)
            {
                continue;
            }

            il.Emit(OpCodes.Ldarg, i + paramStartIndex);
            il.Emit(OpCodes.Ldloc, indirectVariables[i]);
            var directType = managedParams[i].ParameterType.GetElementType();
            EmitConvertManagedTypeToIL2CPP(il, directType);
            il.Emit(
                StIndOpcodes.TryGetValue(directType, out var stindOpCodde)
                    ? stindOpCodde
                    : OpCodes.Stind_I
            );
        }

        // Handle any lingering exceptions
        // il.BeginCatchBlock(typeof(Exception));
        // il.Emit(OpCodes.Call, ReportExceptionMethodInfo);
        // il.EndExceptionBlock();

        // Convert the return value back to an IL2CPP friendly type (if there was a return value), and then return
        if (managedReturnVariable != null)
        {
            if (hasReturnBuffer)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Call, ObjectBaseToPtrNotNullMethodInfo);
                EmitUnbox(il);
                il.Emit(OpCodes.Ldc_I4, returnSize);
                il.Emit(OpCodes.Cpblk);

                // Return the same pointer to the return buffer
                il.Emit(OpCodes.Ldarg_0);
            }
            else
            {
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                EmitConvertManagedTypeToIL2CPP(il, managedReturnType);
            }
        }

        il.Emit(OpCodes.Ret);

        return dynamicMethod;
    }

    private static void EmitUnbox(ILGenerator il)
    {
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sizeof, typeof(void*));
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Add);
    }

    private static void ReportException(Exception ex) =>
        Patcher.Log.LogError($"{ex} During invoking native->managed trampoline");

    private static void EmitConvertManagedTypeToIL2CPP(ILGenerator il, Type returnType)
    {
        if (returnType == typeof(string))
        {
            il.Emit(OpCodes.Call, ManagedToIL2CPPStringMethodInfo);
        }
        else if (!returnType.IsValueType && returnType.IsSubclassOf(typeof(Il2CppObjectBase)))
        {
            il.Emit(OpCodes.Call, ObjectBaseToPtrMethodInfo);
        }
    }

    private static bool EmitConvertArgumentToManaged(
        ILGenerator il,
        int argIndex,
        Type managedParamType,
        bool hasOutAttr,
        out LocalBuilder variable
    )
    {
        variable = null;

        if (managedParamType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            // Box struct into object first before conversion
            var classPtr = Il2CppClassPointerStore.GetNativeClassPointer(managedParamType);
            var nativeType = managedParamType.NativeType();

            Patcher.Log.LogDebug(
                $"{managedParamType.FullName}, native type {nativeType}, ptr {classPtr}"
            );

            il.Emit(OpCodes.Ldc_I8, classPtr.ToInt64());
            il.Emit(OpCodes.Conv_I);
            // On x64, struct is always a pointer but it is a non-pointer on x86
            // We don't handle byref structs on x86 yet but we're yet to encounter those
            il.Emit(Environment.Is64BitProcess ? OpCodes.Ldarg_S : OpCodes.Ldarga_S, argIndex);
            il.Emit(OpCodes.Call, IL2CPPValueBoxMethodInfo);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_S, argIndex);
        }

        if (managedParamType.IsValueType) // don't need to convert blittable types
        {
            return true;
        }

        void EmitCreateIl2CppObject(Type originalType)
        {
            var endLabel = il.DefineLabel();
            var notNullLabel = il.DefineLabel();

            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue_S, notNullLabel);

            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Br_S, endLabel);

            il.MarkLabel(notNullLabel);
            il.Emit(
                OpCodes.Call,
                AccessTools
                    .Method(typeof(Il2CppObjectPool), nameof(Il2CppObjectPool.Get))
                    .MakeGenericMethod(originalType)
            );

            il.MarkLabel(endLabel);
        }

        void HandleTypeConversion(Type originalType)
        {
            if (originalType == typeof(string))
            {
                il.Emit(OpCodes.Call, IL2CPPToManagedStringMethodInfo);
            }
            else if (originalType.IsSubclassOf(typeof(Il2CppObjectBase)))
            {
                EmitCreateIl2CppObject(originalType);
            }
        }

        if (managedParamType.IsByRef)
        {
            // TODO: directType being ValueType is not handled yet (but it's not that common in games). Implement when needed.
            var directType = managedParamType.GetElementType();
            if (directType.IsValueType || directType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
            {
                Patcher.Log.LogDebug("skipping, directType is ValueType, unhandled");
                return false;
            }

            variable = il.DeclareLocal(directType);

            il.Emit(OpCodes.Ldind_I);

            HandleTypeConversion(directType);

            il.Emit(OpCodes.Stloc, variable);
            il.Emit(OpCodes.Ldloca, variable);
        }
        else
        {
            HandleTypeConversion(managedParamType);
        }

        return true;
    }
}
