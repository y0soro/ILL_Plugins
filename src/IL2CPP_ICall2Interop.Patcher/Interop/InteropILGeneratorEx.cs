// adapted from Il2CppInterop.Generator/Extensions/ILGeneratorEx.cs

#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace IL2CPP_ICall2Interop.Patcher.Interop;

// XXX: merge emitters with NativeType() to generate configs and factories instead?
internal static class InteropILGeneratorEx
{
    private static FieldInfo NativeClassPtrField(this Type interopType)
    {
        var classPointerTypeRef = typeof(Il2CppClassPointerStore<>).MakeGenericType(interopType);
        return classPointerTypeRef.GetField(nameof(Il2CppClassPointerStore<>.NativeClassPtr))!;
    }

    private static void EmitNewObj(
        this ILGenerator body,
        Type interopType,
        RuntimeReferences imports,
        bool pool
    )
    {
        if (pool)
        {
            body.Emit(OpCodes.Call, imports.Il2CppObjectPool_Get.MakeGenericMethod(interopType));
        }
        else
        {
            body.Emit(
                OpCodes.Newobj,
                interopType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public,
                    [typeof(nint)]
                )!
            );
        }
    }

    public static void EmitFilterRaiseSEHException(
        this ILGenerator body,
        DmdFixup.EmitFilterState state,
        RuntimeReferences imports
    )
    {
        body.EmitBeginFilterBlock(state);

        Label brIsExceptionLabel = body.DefineLabel();
        Label endFilterLabel = body.DefineLabel();

        body.Emit(OpCodes.Isinst, typeof(Exception));
        body.Emit(OpCodes.Dup);
        body.Emit(OpCodes.Brtrue_S, brIsExceptionLabel);

        body.Emit(OpCodes.Pop);
        body.Emit(OpCodes.Ldc_I4_0);
        body.Emit(OpCodes.Br_S, endFilterLabel);

        body.MarkLabel(brIsExceptionLabel);

        var exVar = body.DeclareLocal(typeof(Exception));

        body.Emit(OpCodes.Stloc, exVar);
        body.Emit(OpCodes.Ldloc, exVar);

        // passthrough SEHException without capturing actual native exception so it can be propagated up to SEH-backed il2cpp C++ try-catch(__try, __except)
        // !(ex is SEHException)
        body.Emit(OpCodes.Isinst, typeof(SEHException));
        body.Emit(OpCodes.Ldnull);
        body.Emit(OpCodes.Ceq);

        body.MarkLabel(endFilterLabel);

        body.EmitBeginFilterHandler(state);

        body.Emit(OpCodes.Pop);
        body.Emit(OpCodes.Ldloc, exVar);

        // map managed Exception message to il2cpp exception and throw to il2cpp scope
        body.Emit(OpCodes.Call, imports.Method_Il2cppRaiseMappedManagedException);

        // mark non-return
        // body.Emit(OpCodes.Rethrow);

        body.EmitEndFilterHandler(state);
    }

    // pop managed value/pointer
    // push native value/pointer
    public static void EmitConvertInteropToNative(
        this ILGenerator body,
        Type interopType,
        bool isOutRef,
        bool isThisParam,
        RuntimeReferences imports,
        out LocalBuilder? refNativeVar,
        bool allocateForRefNullValueTypeWrapper = true
    )
    {
        refNativeVar = null;
        if (interopType.IsGenericParameter)
        {
            throw new InvalidOperationException("GenericParameter should not exists in ICall");
        }

        if (interopType.IsByRef)
        {
            var elemType = interopType.GetElementType()!;
            if (elemType.IsValueTypeLike())
            {
                // native type should also be ValueType with same layout, pass as is
            }
            else if (elemType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
            {
                // native type is ValueType but interop type is boxed Il2CppObjectBase wrapper of that ValueType,

                if (allocateForRefNullValueTypeWrapper)
                {
                    // HACK: always allocate if it's null for ref T extends Il2CppSystem.ValueType

                    var isNonNullLabel = body.DefineLabel();

                    body.Emit(OpCodes.Dup);
                    body.Emit(OpCodes.Ldind_Ref);
                    body.Emit(OpCodes.Ldnull);
                    body.Emit(OpCodes.Bgt, isNonNullLabel);

                    body.Emit(OpCodes.Dup);
                    body.Emit(
                        OpCodes.Newobj,
                        elemType.GetConstructor(BindingFlags.Instance | BindingFlags.Public, [])!
                    );
                    // this wrapper reference is final, store here
                    body.Emit(OpCodes.Stind_Ref);

                    body.MarkLabel(isNonNullLabel);
                }
                else if (isOutRef)
                {
                    // for out parameter, it's always passed in as null, so we need to manually allocate memory for boxed ValueType wrapper

                    body.Emit(OpCodes.Dup);
                    // XXX: pool it?
                    body.Emit(
                        OpCodes.Newobj,
                        elemType.GetConstructor(BindingFlags.Instance | BindingFlags.Public, [])!
                    );
                    // this wrapper reference is final, store here
                    body.Emit(OpCodes.Stind_Ref);
                }

                body.Emit(OpCodes.Ldind_Ref);
                // throw if null, it's just invalid to pass dangling ValueType pointer
                body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull);

                // unbox returns pointer to value part of original native object memory,
                // so we don't need to copy back changes after method invocation
                body.Emit(OpCodes.Call, imports.IL2CPP_il2cpp_object_unbox);
            }
            else
            {
                // interop type is Il2CppObjectBase or string hence native type is native object pointer by interop generator rules

                refNativeVar = body.DeclareLocal(typeof(nint));

                body.Emit(OpCodes.Ldind_Ref);

                if (elemType == typeof(string))
                {
                    body.Emit(OpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp);
                }
                else
                {
                    Debug.Assert(elemType.IsSubclassOf(typeof(Il2CppObjectBase)));
                    body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr);
                }

                body.Emit(OpCodes.Stloc, refNativeVar);
                body.Emit(OpCodes.Ldloca, refNativeVar);
                body.Emit(OpCodes.Conv_I);
            }
        }
        else if (isThisParam && interopType.IsValueType)
        {
            // the underlying type is actually byref of this value type,
            // however that's the same of native type and we don't perform any additional type conversion so just pass as is
        }
        else if (interopType.IsValueTypeLike())
        {
            // same blittable type, pass as is
        }
        else if (interopType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            // native type is ValueType but interop type is boxed Il2CppObjectBase wrapper of that ValueType,
            body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull);
            body.Emit(OpCodes.Call, imports.IL2CPP_il2cpp_object_unbox);

            if (isThisParam)
            {
                // for instance method, value type instance is passed by ref, i.e. pointer
                // unbox above returns pointer to value part of original native object memory,
                // so we don't need to copy back changes after method invocation
            }
            else
            {
                // pass ValueType by value, dotnet runtime will handle hidden pointer conversion by calling convention
                var opaqueStructType = interopType.GetFixedSizeStructType();
                body.Emit(OpCodes.Ldobj, opaqueStructType);
            }
        }
        else if (interopType == typeof(string))
        {
            body.Emit(OpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp);
        }
        else
        {
            Debug.Assert(interopType.IsSubclassOf(typeof(Il2CppObjectBase)));
            body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr);
        }
    }

    // pop managed ref
    public static void EmitUpdateInteropRef(
        this ILGenerator body,
        Type interopType,
        RuntimeReferences imports,
        LocalBuilder refNativeVar
    )
    {
        if (!interopType.IsByRef)
            return;

        var elemType = interopType.GetElementType()!;
        if (elemType.IsValueTypeLike() || elemType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
            return;

        body.Emit(OpCodes.Ldloc, refNativeVar);

        if (elemType == typeof(string))
        {
            body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppStringToManaged);
        }
        else
        {
            Debug.Assert(elemType.IsSubclassOf(typeof(Il2CppObjectBase)));

            // get from pool so interop object remains the same if native pointer not changed
            body.EmitNewObj(elemType, imports, true);
        }

        body.Emit(OpCodes.Stind_Ref);
    }

    private static void EmitWrapNativeValueTypeRef(
        this ILGenerator body,
        Type interopType,
        RuntimeReferences imports,
        bool pool
    )
    {
        var tempVar = body.DeclareLocal(typeof(nint));

        body.Emit(OpCodes.Stloc, tempVar);

        body.Emit(OpCodes.Ldsfld, interopType.NativeClassPtrField());
        body.Emit(OpCodes.Ldloc, tempVar);
        body.Emit(OpCodes.Call, imports.IL2CPP_il2cpp_value_box);

        body.EmitNewObj(interopType, imports, pool);
    }

    // pop native value/pointer
    // push managed value/pointer
    public static void EmitConvertNativeToInterop(
        this ILGenerator body,
        Type interopType,
        bool isOutRef,
        bool isThisParam,
        RuntimeReferences imports,
        out LocalBuilder? refInteropVar
    )
    {
        refInteropVar = null;
        if (interopType.IsGenericParameter)
        {
            // TODO: also check generic type recursively
            throw new InvalidOperationException("GenericParameter should not exists in ICall");
        }

        // no pool to improve performance
        var poolValueTypeWrapper = false;

        if (interopType.IsByRef)
        {
            var elemType = interopType.GetElementType()!;
            if (elemType.IsValueTypeLike())
            {
                // native type should also be ValueType with same layout, pass as is
                return;
            }

            if (elemType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
            {
                // XXX: assumes non-null, add check if necessary

                if (isOutRef)
                {
                    body.Emit(OpCodes.Pop);
                    // InteropToNative will initialize a new object for this out parameter anyway, so just push null
                    body.Emit(OpCodes.Ldnull);
                }
                else
                {
                    body.EmitWrapNativeValueTypeRef(elemType, imports, poolValueTypeWrapper);
                }

                // il2cpp_value_box creates a boxed copy, so we need to copy changes back to the original memory for ref
            }
            else if (elemType == typeof(string))
            {
                body.Emit(OpCodes.Ldind_I);
                body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppStringToManaged);
            }
            else
            {
                Debug.Assert(elemType.IsSubclassOf(typeof(Il2CppObjectBase)));

                body.Emit(OpCodes.Ldind_I);

                body.EmitNewObj(elemType, imports, true);
            }

            refInteropVar = body.DeclareLocal(elemType);

            body.Emit(OpCodes.Stloc, refInteropVar);
            body.Emit(OpCodes.Ldloca, refInteropVar);
        }
        else if (interopType.IsValueTypeLike())
        {
            // same blittable type, pass as is
        }
        else if (isThisParam && interopType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            // native type is by ref of value type
            body.EmitWrapNativeValueTypeRef(interopType, imports, poolValueTypeWrapper);

            refInteropVar = body.DeclareLocal(interopType);

            body.Emit(OpCodes.Stloc, refInteropVar);
            // converted interop type is still wrapper reference
            body.Emit(OpCodes.Ldloc, refInteropVar);
        }
        else if (interopType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            var opaqueStructType = interopType.GetFixedSizeStructType();
            var tempVar = body.DeclareLocal(opaqueStructType);

            body.Emit(OpCodes.Stloc, tempVar);

            body.Emit(OpCodes.Ldsfld, interopType.NativeClassPtrField());
            // native type passed by value, so load address instead
            body.Emit(OpCodes.Ldloca, tempVar);
            body.Emit(OpCodes.Conv_I);
            body.Emit(OpCodes.Call, imports.IL2CPP_il2cpp_value_box);

            body.EmitNewObj(interopType, imports, poolValueTypeWrapper);
        }
        else if (interopType == typeof(string))
        {
            body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppStringToManaged);
        }
        else
        {
            Debug.Assert(interopType.IsSubclassOf(typeof(Il2CppObjectBase)));
            body.EmitNewObj(interopType, imports, true);
        }
    }

    // pop native pointer
    public static void EmitUpdateNativeRef(
        this ILGenerator body,
        Type interopType,
        bool isThisParam,
        RuntimeReferences imports,
        LocalBuilder refInteropVar
    )
    {
        Type elemType;
        if (interopType.IsByRef)
        {
            elemType = interopType.GetElementType()!;
            if (elemType.IsValueTypeLike())
                return;
        }
        else if (isThisParam && interopType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            elemType = interopType;
        }
        else
        {
            return;
        }

        body.Emit(OpCodes.Ldloc, refInteropVar);

        if (elemType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull);
            body.Emit(OpCodes.Call, imports.IL2CPP_il2cpp_object_unbox);

            // il2cpp_value_box creates a boxed copy, so we need to copy changes back to the original memory for ref
            body.Emit(OpCodes.Ldc_I4, elemType.GetNativeSize());
            body.Emit(OpCodes.Cpblk);

            // or just cpobj, but this might fail if struct alignment(pack) is incorrect or pointers are not aligned in the first place
            //body.Emit(OpCodes.Cpobj, interopType.GetFixedSizeStructType());
        }
        else if (elemType == typeof(string))
        {
            body.Emit(OpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp);
            body.Emit(OpCodes.Stind_I);
        }
        else
        {
            Debug.Assert(elemType.IsSubclassOf(typeof(Il2CppObjectBase)));
            body.Emit(OpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr);
            body.Emit(OpCodes.Stind_I);
        }
    }
}
