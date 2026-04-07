#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace IL2CPP_ICall2Interop.Patcher.Interop;

internal static class Helper
{
    private static AssemblyBuilder? _fixedStructAssembly;
    private static ModuleBuilder? _fixedStructModuleBuilder;
    private static readonly Dictionary<int, Type> _fixedStructCache = [];

    private static Type GetFixedSizeStructType(int size, uint align)
    {
        if (_fixedStructCache.TryGetValue(size, out var result))
        {
            return result;
        }

        _fixedStructAssembly ??= AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ICall2Interop_FixedSizeStructAssembly"),
            AssemblyBuilderAccess.Run
        );
        _fixedStructModuleBuilder ??= _fixedStructAssembly.DefineDynamicModule(
            "ICall2Interop_FixedSizeStructAssembly"
        );

        var pack = (PackingSize)align;
        if (!Enum.IsDefined(pack))
        {
            throw new InvalidOperationException($"invalid struct alignment {align}");
        }

        var tb = _fixedStructModuleBuilder.DefineType(
            $"ICall2Interop_FixedSizeStruct_{size}b",
            TypeAttributes.ExplicitLayout,
            typeof(ValueType),
            pack,
            size
        );

        var type = tb.CreateType()!;
        return _fixedStructCache[size] = type;
    }

    public static Type GetFixedSizeStructType(this Type interopType)
    {
        uint align = 0;
        var fixedSize = IL2CPP.il2cpp_class_value_size(
            Il2CppClassPointerStore.GetNativeClassPointer(interopType),
            ref align
        );
        return GetFixedSizeStructType(fixedSize, align);
    }

    public static int GetNativeSize(this Type interopType)
    {
        uint _align = 0;
        return IL2CPP.il2cpp_class_value_size(
            Il2CppClassPointerStore.GetNativeClassPointer(interopType),
            ref _align
        );
    }

    public static Type NativeType(this Type interopType, bool isThisParam = false)
    {
        if (interopType.IsGenericParameter)
        {
            throw new InvalidOperationException("GenericParameter should not exists in ICall");
        }

        if (interopType.IsByRef)
        {
            var elemType = interopType.GetElementType()!;

            if (interopType == typeof(bool))
            {
                return typeof(byte).MakeByRefType();
            }
            else if (elemType.IsValueTypeLike())
            {
                // blittable, pass as is
                return interopType;
            }
            else if (elemType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
            {
                // manual unmanaged conversion is required so use pointer type instead of ref FixedSizeStruct for simplicity
                return typeof(nint);
            }
            else
            {
                Debug.Assert(
                    elemType.IsSubclassOf(typeof(Il2CppObjectBase)) || elemType == typeof(string)
                );
                // unmanaged Il2CppObject**, just use pointer type here
                return typeof(nint);
            }
        }
        else if (interopType == typeof(bool))
        {
            return typeof(byte);
        }
        else if (isThisParam && interopType.IsValueType)
        {
            // for instance method, value type instance is passed by ref, i.e. pointer
            return interopType.MakeByRefType();
        }
        else if (interopType.IsValueTypeLike())
        {
            // blittable, pass as is
            return interopType;
        }
        else if (interopType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            // for instance method, value type instance is passed by ref, i.e. pointer
            if (isThisParam)
                return typeof(nint);

            // pass ValueType by value, dotnet runtime will handle hidden pointer conversion by calling convention
            return GetFixedSizeStructType(interopType);
        }
        else
        {
            if (
                !interopType.IsSubclassOf(typeof(Il2CppObjectBase))
                && interopType != typeof(string)
            )
                throw new ArgumentException($"unhandled interop type {interopType}");

            // unmanaged Il2CppObject*, just use pointer type here
            return typeof(nint);
        }
    }

    public static bool IsPointerLike(this Type type) => type.IsPointer || type.IsByRef;

    public static bool IsValueTypeLike(this Type type) => type.IsValueType || type.IsPointerLike();
}
