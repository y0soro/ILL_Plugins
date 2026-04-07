#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using MonoMod.Utils;
using MonoMod.Utils.Cil;

namespace IL2CPP_ICall2Interop.Patcher.Interop;

internal static class DmdFixup
{
    public static MethodInfo GenerateCecil(this DynamicMethodDefinition dmd, string? dumpDir = null)
    {
        dumpDir ??= Patcher.DumpDir;

        var oldDmdDump = Environment.GetEnvironmentVariable("MONOMOD_DMD_DUMP");
        var oldDmdType = Environment.GetEnvironmentVariable("MONOMOD_DMD_TYPE");
        Environment.SetEnvironmentVariable("MONOMOD_DMD_DUMP", dumpDir);
        Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", "cecil");
        try
        {
            return dmd.Generate();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MONOMOD_DMD_DUMP", oldDmdDump);
            Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", oldDmdType);
        }
    }

    private static MethodInfo? EmitMethod;

    public static void EmitCalliImpl(
        this ILGenerator body,
        CallingConvention callingConvention,
        Type returnType,
        IEnumerable<Type> parameterTypes
    )
    {
        var generator = body.GetProxiedShim<CecilILGenerator>()!;
        var module = generator.IL.Body.Method.Module;

        var callSite = new Mono.Cecil.CallSite(module.ImportReference(returnType))
        {
            CallingConvention = (Mono.Cecil.MethodCallingConvention)((int)callingConvention - 1),
        };

        foreach (var paramType in parameterTypes)
        {
            callSite.Parameters.Add(new(module.ImportReference(paramType)));
        }

        var ins = generator.IL.Create(Mono.Cecil.Cil.OpCodes.Calli, callSite)!;

        EmitMethod ??= AccessTools.Method(
            typeof(CecilILGenerator),
            "Emit",
            [typeof(Mono.Cecil.Cil.Instruction)]
        );

        EmitMethod.Invoke(generator, [ins]);
    }

    public class EmitFilterState(CecilILGenerator il)
    {
        public readonly Label TryStart = il.DefineLabel();
        public readonly Label FilterStart = il.DefineLabel();
        public readonly Label HandlerStart = il.DefineLabel();
        public readonly Label HandlerEnd = il.DefineLabel();
    }

    public static EmitFilterState EmitBeginFilterTry(this ILGenerator body)
    {
        var il = body.GetProxiedShim<CecilILGenerator>()!;

        var state = new EmitFilterState(il);

        il.MarkLabel(state.TryStart);

        return state;
    }

    public static void EmitBeginFilterBlock(this ILGenerator body, EmitFilterState state)
    {
        var il = body.GetProxiedShim<CecilILGenerator>()!;

        il.Emit(OpCodes.Leave_S, state.HandlerEnd);

        il.MarkLabel(state.FilterStart);
    }

    public static void EmitBeginFilterHandler(this ILGenerator body, EmitFilterState state)
    {
        var il = body.GetProxiedShim<CecilILGenerator>()!;

        il.Emit(OpCodes.Endfilter);
        il.MarkLabel(state.HandlerStart);
    }

    private static MethodInfo? GetLabelInfoMethod;
    private static FieldInfo? LabelInstructionField;

    private static Mono.Cecil.Cil.Instruction LabelInstruction(
        this CecilILGenerator il,
        Label label
    )
    {
        GetLabelInfoMethod ??= AccessTools.Method(typeof(CecilILGenerator), "_", [typeof(Label)]);
        LabelInstructionField ??= AccessTools.Field(GetLabelInfoMethod.ReturnType, "Instruction");

        var info = GetLabelInfoMethod.Invoke(il, [label])!;

        return (Mono.Cecil.Cil.Instruction)LabelInstructionField.GetValue(info)!;
    }

    public static void EmitEndFilterHandler(this ILGenerator body, EmitFilterState state)
    {
        var il = body.GetProxiedShim<CecilILGenerator>()!;
        var handlers = il.IL.Body.ExceptionHandlers;

        il.Emit(OpCodes.Leave_S, state.HandlerEnd);

        il.MarkLabel(state.HandlerEnd);
        // make label above resolvable
        il.Emit(OpCodes.Nop);

        handlers.Add(
            new(Mono.Cecil.Cil.ExceptionHandlerType.Filter)
            {
                TryStart = il.LabelInstruction(state.TryStart),
                TryEnd = il.LabelInstruction(state.FilterStart),
                FilterStart = il.LabelInstruction(state.FilterStart),
                HandlerStart = il.LabelInstruction(state.HandlerStart),
                HandlerEnd = il.LabelInstruction(state.HandlerEnd),
            }
        );
    }
}
