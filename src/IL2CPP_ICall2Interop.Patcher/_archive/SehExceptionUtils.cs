#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IL2CPP_ICall2Interop.Patcher.Interop;
using Il2CppInterop.Runtime;

namespace IL2CPP_ICall2Interop.Patcher;

// unused, archival
//
// the original plan is to rethrow native SEHException in try-catch handler,
// but turns out we can just filter out mapped SEHException with try-filter
internal static class SehExceptionUtils
{
    const uint SEH_VISUAL_CPP_EXCEPTION_CODE = 0xe06d7363;

    public static unsafe void PrintIl2CppException()
    {
        var ptr = Marshal.GetExceptionPointers();

        var pointers = Unsafe.AsRef<EXCEPTION_POINTERS64>((void*)ptr);
        var record = Unsafe.AsRef<EXCEPTION_RECORD64>(pointers.exceptionRecord);

        var code = record.ExceptionCode;
        var argCnt = record.NumberParameters;
        var args = record.ExceptionInformation;

        if (code != SEH_VISUAL_CPP_EXCEPTION_CODE)
            return;

        var exName = GetExceptionTypeName(pointers.exceptionRecord);
        if (exName[0] != ".?AUIl2CppExceptionWrapper@@")
            return;

        var wrapper = (nint*)args[1];
        var exPtr = *wrapper;

        var il2cppEx = new Il2CppException(exPtr);

        Patcher.Log.LogDebug(
            $"exception code 0x{code:x} name: {exName[0]} {exName.Count} chain:{(nint)record.pExceptionRecord:x}"
        );
        Patcher.Log.LogDebug($"{il2cppEx.Message}");
    }

    [DllImport("kernel32.dll")]
    private static extern unsafe void RaiseException(
        uint dwExceptionCode,
        uint dwExceptionFlags,
        uint nNumberOfArguments,
        nint* lpArguments
    );

    [DoesNotReturn]
    public static unsafe void ReThrowSEHException()
    {
        var ptr = Marshal.GetExceptionPointers();
        var pointers = Unsafe.AsRef<EXCEPTION_POINTERS64>((void*)ptr);
        var record = Unsafe.AsRef<EXCEPTION_RECORD64>(pointers.exceptionRecord);
        RaiseException(
            record.ExceptionCode,
            record.ExceptionFlags,
            record.NumberParameters,
            (nint*)record.ExceptionInformation
        );
    }

    public static unsafe void ReThrowIl2cppException()
    {
        var ptr = Marshal.GetExceptionPointers();

        var pointers = Unsafe.AsRef<EXCEPTION_POINTERS64>((void*)ptr);
        var record = Unsafe.AsRef<EXCEPTION_RECORD64>(pointers.exceptionRecord);

        var code = record.ExceptionCode;
        var argCnt = record.NumberParameters;
        var args = record.ExceptionInformation;

        if (code != SEH_VISUAL_CPP_EXCEPTION_CODE)
            return;

        var exName = GetExceptionTypeName(pointers.exceptionRecord);
        if (exName[0] != ".?AUIl2CppExceptionWrapper@@")
            return;

        var wrapper = (nint*)args[1];
        var exPtr = *wrapper;

        RuntimeReferences.il2cpp_raise_exception(exPtr);
    }

    public static unsafe List<string> GetExceptionTypeName(EXCEPTION_RECORD64* recordPtr)
    {
        var record = Unsafe.AsRef<EXCEPTION_RECORD64>(recordPtr);
        // 0: magic number, 1: original exception pointer, 2: ThrowInfo, 3: dll image base
        // see https://devblogs.microsoft.com/oldnewthing/20100730-00/?p=13273
        // and EHExceptionRecord in ehdata.h
        var args = record.ExceptionInformation;
        var throwInfo = Unsafe.AsRef<VcppThrowInfo>((void*)args[2]);
        var pImage = args[3];

        var pTypeArray = pImage + (ulong)throwInfo.pCatchableTypeArray;
        var typeArray = Unsafe.AsRef<VcppCatchableTypeArray>((void*)pTypeArray);

        var pTypeArrayData = pTypeArray + (ulong)sizeof(VcppCatchableTypeArray);
        var typeArraySpan = new Span<int>((void*)pTypeArrayData, typeArray.nCatchableTypes);

        List<string> res = [];

        foreach (var typeOffset in typeArraySpan)
        {
            var pTypeInfo = pImage + (ulong)typeOffset;
            var typeInfo = Unsafe.AsRef<VcppCatchableType>((void*)pTypeInfo);

            var pTypeName = pImage + (ulong)typeInfo.pType + (ulong)sizeof(VcppTypeDescriptor);

            var name = Marshal.PtrToStringAnsi((nint)pTypeName);
            res.Add(name);
        }

        return res;
    }
}

// see https://github.com/microsoft/CLRInstrumentationEngine/blob/main/src/unix/inc/ehdata.h
//     or ehdata*.h in MSVC CRT include (download with https://github.com/Jake-Shadle/xwin),
//     _EH_RELATIVE_OFFSETS is true for win64

struct VcppPMFN
{
    public int offset; // Offset of intended data within base
}

struct VcppPMD
{
    public int mdisp; // Offset of intended data within base
    public int pdisp; // Displacement to virtual base pointer
    public int vdisp; // Index within vbTable to offset of base
}

struct VcppTypeDescriptor
{
    public nint pVFTable; // Field overloaded by RTTI
    public nint spare; // reserved, possible for RTTI
    // char name [];
    // zero-sized null-terminated string at end
}

struct VcppCatchableType
{
    public uint properties; // Catchable Type properties (Bit field)
    public int pType; // Image relative offset of TypeDescriptor
    public VcppPMD thisDisplacement; // Pointer to instance of catch type within thrown object.
    public int sizeOrOffset; // Size of simple-type object or offset into
    public VcppPMFN copyFunction; // Copy constructor or CC-closure
}

struct VcppCatchableTypeArray
{
    public int nCatchableTypes;
    // __int32			arrayOfCatchableTypes[]; // Image relative offset of Catchable Types

    // zero-sized array of relative offset of Catchable Types at end
}

struct VcppThrowInfo
{
    public uint attributes; // Throw Info attributes (Bit field)
    public VcppPMFN pmfnUnwind; // Destructor to call when exception
    public int pForwardCompat; // Image relative offset of Forward compatibility frame handler
    public int pCatchableTypeArray; // Image relative offset of CatchableTypeArray
}

internal unsafe struct EXCEPTION_POINTERS64
{
    public EXCEPTION_RECORD64* exceptionRecord;
    public CONTEXT* contextRecord;
}

internal unsafe struct EXCEPTION_RECORD64
{
    public uint ExceptionCode;
    public uint ExceptionFlags;
    public EXCEPTION_RECORD64* pExceptionRecord;
    public nint ExceptionAddress;
    public uint NumberParameters;
    public fixed ulong ExceptionInformation[15];
}

internal struct CONTEXT
{
    public uint ContextFlags;
    public uint Dr0;
    public uint Dr1;
    public uint Dr2;
    public uint Dr3;
    public uint Dr6;
    public uint Dr7;
    public FLOATING_SAVE_AREA FloatSave;
    public uint SegGs;
    public uint SegFs;
    public uint SegEs;
    public uint SegDs;
    public uint Edi;
    public uint Esi;
    public uint Ebx;
    public uint Edx;
    public uint Ecx;
    public uint Eax;
    public uint Ebp;
    public uint Eip;
    public uint SegCs;
    public uint EFlags;
    public uint Esp;
    public uint SegSs;
};

internal struct FLOATING_SAVE_AREA
{
    public uint ControlWord;
    public uint StatusWord;
    public uint TagWord;
    public uint ErrorOffset;
    public uint ErrorSelector;
    public uint DataOffset;
    public uint DataSelector;
    public byte RegisterArea;
    public uint Cr0NpxState;
};
