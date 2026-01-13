using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;

namespace ILL_SliderUnlocker;

internal static class GetInfoHook
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate byte GetInfoDelegate(
        nint pThis,
        nint pName,
        float rate,
        Vector3* pPos,
        Vector3* pRot,
        Vector3* pScale,
        nint pFlags
    );

    private static INativeDetour detour = null;

    public static unsafe void Install(ulong pGetInfo)
    {
        detour?.Free();
        detour = INativeDetour.Create((nint)pGetInfo, (GetInfoDelegate)GetInfoDetour);
        detour.Apply();
    }

    // Il2CppInterop.HarmonySupport generates wrong IL codes for converting blittable value type pointer(i.e. ref Vector3),
    // so manually detour the function instead
    private static unsafe byte GetInfoDetour(
        nint pThis,
        nint pName,
        float rate,
        Vector3* pPos,
        Vector3* pRot,
        Vector3* pScale,
        nint pFlags
    )
    {
        var rateClamped = rate;
        if (rate > 1)
        {
            rateClamped = 1;
        }
        else if (rate < 0)
        {
            rateClamped = 0;
        }

        var trampoline = (delegate* unmanaged[Cdecl]<
            nint,
            nint,
            float,
            Vector3*,
            Vector3*,
            Vector3*,
            nint,
            byte>)
            detour.TrampolinePtr;

        var ret = trampoline(pThis, pName, rateClamped, pPos, pRot, pScale, pFlags);
        if (ret == 0 || rate >= 0 && rate <= 1)
            return ret;

        var flags = Il2CppObjectPool.Get<Il2CppStructArray<bool>>(pFlags);

        var name = IL2CPP.Il2CppStringToManaged(pName);

        ref var posOut = ref Unsafe.AsRef<Vector3>(pPos);
        ref var rotOut = ref Unsafe.AsRef<Vector3>(pRot);
        ref var scaleOut = ref Unsafe.AsRef<Vector3>(pScale);

#if AC
        var ctrl = Il2CppObjectPool.Get<ILLGAMES.Unity.AnimationKeyInfo.Controller>(pThis);
        var dictInfo = ctrl._dictInfo;
#else
        var ctrl = Il2CppObjectPool.Get<ILLGames.Unity.AnimationKeyInfo.Controller>(pThis);
        var dictInfo = ctrl.dictInfo;
#endif

        if (!dictInfo.TryGetValue(name, out var keyFrames) || keyFrames.Count < 2)
        {
            return ret;
        }

        var first = keyFrames[0];
        var last = keyFrames[^1];

        var doPos = flags[0];
        var doRot = flags[1];
        var doScale = flags[2];

        if (doPos)
        {
            posOut = LerpUnclamped(first.Pos, last.Pos, rate);
        }

        if (doRot)
        {
            Quaternion middle;

            if (keyFrames.Count % 2 == 0)
            {
                middle = Quaternion.SlerpUnclamped(
                    Quaternion.Euler(keyFrames[(keyFrames.Count - 1) / 2].Rot),
                    Quaternion.Euler(keyFrames[keyFrames.Count / 2].Rot),
                    0.5f
                );
            }
            else
            {
                middle = Quaternion.Euler(keyFrames[keyFrames.Count / 2].Rot);
            }

            Quaternion slerpBegin;
            Quaternion slerpEnd;
            float slerpRate;

            if (rate < 0.5f)
            {
                slerpBegin = Quaternion.Euler(first.Rot);
                slerpEnd = middle;
                slerpRate = rate * 2f;
            }
            else
            {
                slerpBegin = middle;
                slerpEnd = Quaternion.Euler(last.Rot);
                slerpRate = (rate - 0.5f) * 2f;
            }

            var slerp = Quaternion.SlerpUnclamped(slerpBegin, slerpEnd, slerpRate);
            rotOut = slerp.eulerAngles;
        }

        if (doScale)
        {
            scaleOut = LerpUnclamped(first.Scl, last.Scl, rate);
        }

        return ret;
    }

    private static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
    {
        return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
    }
}
