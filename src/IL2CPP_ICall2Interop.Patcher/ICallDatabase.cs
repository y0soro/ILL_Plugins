#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mono.Cecil;
using Mono.Cecil.Rocks;

namespace IL2CPP_ICall2Interop.Patcher;

internal class ICallDatabase
{
    public record class Descriptor
    {
        public string Assembly { get; set; } = "";
        public string DeclaringType { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsStatic { get; set; } = false;
        public string ReturnType { get; set; } = "";
        public string[] ParameterTypes { get; set; } = [];
    }

    public readonly ReadOnlyDictionary<string, Descriptor> ByMethodName;

    public ICallDatabase(IDictionary<string, Descriptor> byMethodName)
    {
        ByMethodName = new(byMethodName);
    }

    public bool TryGetDescriptor(string iCallName, out Descriptor? descriptor)
    {
        return ByMethodName.TryGetValue(iCallName, out descriptor);
    }

    public static MethodInfo? ResolveMethodByDescriptor(Descriptor descriptor)
    {
        // assumes no generic type name
        var typeName = $"{descriptor.DeclaringType.Replace('/', '+')}, {descriptor.Assembly}";

        var type = Type.GetType(typeName);
        if (type == null)
        {
            return null;
        }

        var binding = descriptor.IsStatic ? BindingFlags.Static : BindingFlags.Instance;
        binding |=
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly
            | BindingFlags.GetProperty
            | BindingFlags.SetProperty;

        foreach (var method in type.GetMethods(binding))
        {
            var matched = true;
            var parameters = method.GetParameters();

            // internal call should not be generic
            if (method.IsGenericMethod)
                continue;

            if (method.Name != descriptor.Name)
                continue;

            if (parameters.Length != descriptor.ParameterTypes.Length)
                continue;

            if (descriptor.ReturnType != GetTypeCecilFullName(method.ReturnType))
                continue;

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;
                var actualName = GetTypeCecilFullName(parameterType);
                var expectedName = descriptor.ParameterTypes[i];

                if (expectedName != actualName)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return method;
        }

        return null;
    }

    public bool TryGetMethodInfo(string iCallName, out MethodInfo? methodInfo)
    {
        if (!ByMethodName.TryGetValue(iCallName, out var descriptor))
        {
            methodInfo = null;
            return false;
        }

        methodInfo = ResolveMethodByDescriptor(descriptor);
        return true;
    }

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void SaveToFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir!);

        using var file = File.OpenWrite(filePath);

        JsonSerializer.Serialize(file, ByMethodName, jsonOptions);
    }

    public static ICallDatabase? LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using var file = File.OpenRead(filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, Descriptor>>(
                file,
                jsonOptions
            );
            if (data == null)
                return null;

            return new ICallDatabase(data);
        }
        catch
        {
            return null;
        }
    }

    public static ICallDatabase Collect(string? unityLibsDir, string cachePath)
    {
        var res = LoadFromFile(cachePath);
        if (res != null)
            return res;

        if (string.IsNullOrWhiteSpace(unityLibsDir))
            return new ICallDatabase(new Dictionary<string, Descriptor>());

        res = CollectFromUnityLibs(unityLibsDir);
        res.SaveToFile(cachePath);
        return res;
    }

    private static IEnumerable<(MethodDefinition, TypeDefinition, AssemblyDefinition)> EnumMethods(
        IEnumerable<AssemblyDefinition> assemblies
    )
    {
        foreach (var assembly in assemblies)
        {
            if (!assembly.Name.Name.StartsWith("Unity"))
                continue;

            foreach (var type in assembly.MainModule.GetTypes())
            {
                if (type.IsInterface)
                    continue;

                foreach (var method in type.Methods)
                {
                    yield return (method.Resolve(), type, assembly);
                }
            }
        }
    }

    public static ICallDatabase CollectFromUnityLibs(string unityLibsDir)
    {
        var files = Directory.GetFiles(unityLibsDir, "*.dll");

        Dictionary<string, Descriptor> res = [];

        var assemblies = files.Select(AssemblyDefinition.ReadAssembly);

        foreach (var (method, type, assembly) in EnumMethods(assemblies))
        {
            if (method.IsGenericInstance)
                continue;

            if (!method.ImplAttributes.HasFlag(Mono.Cecil.MethodImplAttributes.InternalCall))
                continue;

            var descriptor = new Descriptor
            {
                Assembly = assembly.Name.Name,
                DeclaringType = type.FullName,
                Name = method.Name,
                IsStatic = method.IsStatic,
                ReturnType = RewriteTypeToInterop(method.ReturnType).FullName,
                ParameterTypes =
                [
                    .. method.Parameters.Select(i =>
                        RewriteTypeToInterop(i.ParameterType).FullName
                    ),
                ],
            };

            var iCallName = $"{descriptor.DeclaringType}::{descriptor.Name}";

            if (res.ContainsKey(iCallName))
            {
                throw new NotImplementedException($"duplicate icall name {iCallName}");
            }

            res[iCallName] = descriptor;

            // Patcher.Log.LogInfo($"{iCallName}, {descriptor.Assembly}");

            // foreach (var param in method.Parameters)
            // {
            //     var cvt = RewriteTypeToInterop(param.ParameterType);
            //     var changed = param.ParameterType.FullName != cvt.FullName;
            //     if (changed)
            //         Patcher.Log.LogInfo(
            //             "changed: " + param.ParameterType.FullName + " -> " + cvt.FullName
            //         );
            // }
        }

        return new ICallDatabase(res);
    }

    // XXX: alternatively, import type to TypeReference and get FullName,
    // for Il2CppReferenceArray`1 and Il2CppStructArray`1, just reaplce them with a same identifier(e.g. Il2Cpp_XXX_Array`1) for comparison
    private static string GetTypeCecilFullName(Type type)
    {
        if (type.IsByRef)
            return $"{GetTypeCecilFullName(type.GetElementType()!)}&";

        if (type.IsPointer)
            return $"{GetTypeCecilFullName(type.GetElementType()!)}*";

        if (type.IsArray)
            return $"{GetTypeCecilFullName(type.GetElementType()!)}[]";

        if (type.IsGenericParameter)
            return type.Name;

        string name;
        if (type.DeclaringType != null)
        {
            name = $"{GetTypeCecilFullName(type.DeclaringType)}/{type.Name}";
        }
        else
        {
            name = !string.IsNullOrEmpty(type.Namespace)
                ? $"{type.Namespace}.{type.Name}"
                : type.Name;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var args = type.GetGenericArguments();
            var argNames = string.Join(",", args.Select(GetTypeCecilFullName));

            // fixup for Il2CppSystem.ValueType marked ValueType wrapper
            if (
                type.GetGenericTypeDefinition() == typeof(Il2CppReferenceArray<>)
                && args[0].IsSubclassOf(typeof(Il2CppSystem.ValueType))
            )
            {
                name = GetTypeCecilFullName(typeof(Il2CppStructArray<>));
            }

            return $"{name}<{argNames}>";
        }

        return name;
    }

    // see Il2CppInterop.Generator.Contexts.AssemblyRewriteContext::RewriteTypeRef
    // we only care about final type full name here
    private static TypeReference RewriteTypeToInterop(TypeReference type)
    {
        var module = type.Module;

        if (type is ByReferenceType byRef)
            return RewriteTypeToInterop(byRef.ElementType).MakeByReferenceType();

        if (type is PointerType ptr)
            return RewriteTypeToInterop(ptr.ElementType).MakePointerType();

        if (type is ArrayType arr)
        {
            if (arr.Rank > 1)
                return module.ImportReference(typeof(Il2CppArrayBase));

            var elem = arr.ElementType;

            if (elem.FullName == "System.String")
                return module.ImportReference(typeof(Il2CppStringArray));

            var convertedElem = RewriteTypeToInterop(elem);

            if (elem.IsGenericParameter)
                return module
                    .ImportReference(typeof(Il2CppArrayBase<>))
                    .MakeGenericInstanceType(convertedElem);

            return module
                .ImportReference(
                    convertedElem.IsValueType
                        ? typeof(Il2CppStructArray<>)
                        : typeof(Il2CppReferenceArray<>)
                )
                .MakeGenericInstanceType(convertedElem);
        }

        if (type is GenericInstanceType genericInstance)
        {
            var genericType = RewriteTypeToInterop(genericInstance.ElementType);
            var instance = new GenericInstanceType(genericType);

            foreach (var arg in genericInstance.GenericArguments)
            {
                instance.GenericArguments.Add(RewriteTypeToInterop(arg));
            }

            return instance;
        }

        if (type is GenericParameter genericParam)
        {
            return new GenericParameter(genericParam.Name, genericParam.Owner);
        }

        if (type.IsPrimitive)
            return type;

        string[] passThroughTypes = ["System.TypedReference", "System.Void", "System.String"];
        if (passThroughTypes.Any(i => i == type.FullName))
            return type;

        var needPrefix = type.Namespace == "System" || type.Namespace.StartsWith("System.");

        var res = new TypeReference(
            needPrefix ? $"Il2Cpp{type.Namespace}" : type.Namespace,
            type.Name,
            module,
            type.Scope,
            type.IsValueType
        );

        if (type.DeclaringType != null)
            res.DeclaringType = RewriteTypeToInterop(type.DeclaringType);

        if (type.HasGenericParameters)
        {
            foreach (var i in type.GenericParameters)
            {
                res.GenericParameters.Add(new GenericParameter(i.Name, res));
            }
        }

        return res;
    }
}
