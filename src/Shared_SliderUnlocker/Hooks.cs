using System;
using System.Globalization;
using Character;
using CharacterCreation;
using HarmonyLib;
using UnityEngine.UI;
#if AC
using R3;
#else
using UniRx;
# endif

namespace ILL_SliderUnlocker;

public partial class Plugin
{
    internal static class Hooks
    {
#if !DC
        // // optional, clamp patcher already removes ratio clamping in underlying native code
        // [HarmonyPrefix]
        // [HarmonyPatch(typeof(HumanCustom), nameof(HumanCustom.ConvertTextFromRate01))]
        // private static bool ConvertTextFromRate01(ref string __result, float value)
        // {
        //     __result = Math.Round(100f * value).ToString(CultureInfo.InvariantCulture);
        //     Log.LogDebug($"rate: {value}, text: {__result}");
        //     return false;
        // }

        // [HarmonyPostfix]
        // [HarmonyPatch(typeof(HumanCustom), nameof(HumanCustom.ConvertTextFromRate))]
        // private static void ConvertTextFromRate(ref string __result, int min, int max, float value)
        // {
        //     if (min == 0 && max == 100)
        //     {
        //         __result = Math.Round(100f * value).ToString(CultureInfo.InvariantCulture);
        //     }
        // }

        [HarmonyPostfix]
        [HarmonyPatch(
            typeof(HumanCustom),
            nameof(HumanCustom.ConvertRateFromText),
            [typeof(int), typeof(int), typeof(string)]
        )]
        private static void ConvertRateFromText(ref float __result, int min, int max, string buf)
        {
            if (min == 0 && max == 100)
            {
                ConvertRateFromText(ref __result, buf);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(
            typeof(HumanCustom),
            nameof(HumanCustom.ConvertRateFromText),
            [typeof(string)]
        )]
        private static void ConvertRateFromText(ref float __result, string buf)
        {
            if (buf == null || buf == "")
            {
                __result = 0f;
            }
            else if (!float.TryParse(buf, out float result))
            {
                __result = 0f;
            }
            else
            {
                __result = result / 100f;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(
            typeof(HumanCustom),
            nameof(HumanCustom.EntryUndo),
            [
                typeof(HumanCustom.IInputSlider),
                typeof(Il2CppSystem.Func<float>),
                typeof(Il2CppSystem.Func<float, bool>),
                typeof(CompositeDisposable),
            ]
        )]
        private static void EntryUndo(HumanCustom.IInputSlider pack)
        {
            SetSliderRange(pack.Slider);
        }

        [HarmonyPrefix]
        [HarmonyPatch(
            typeof(HumanCustom),
            nameof(HumanCustom.EntryUndo),
            [
                typeof(HumanCustom.ISlider),
                typeof(Il2CppSystem.Func<float>),
                typeof(Il2CppSystem.Action<float>),
                typeof(CompositeDisposable),
            ]
        )]
        private static void EntryUndo(HumanCustom.ISlider pack)
        {
            SetSliderRange(pack.Slider);
        }

        [HarmonyPrefix]
        [HarmonyPatch(
            typeof(HumanCustom),
            nameof(HumanCustom.EntryUndo),
            [
                typeof(HumanCustom.IInputSliderButton),
                typeof(Il2CppSystem.Func<float>),
                typeof(Il2CppSystem.Func<float, bool>),
                typeof(Il2CppSystem.Func<HumanData, float>),
                typeof(CompositeDisposable),
            ]
        )]
        private static void EntryUndo(HumanCustom.IInputSliderButton pack)
        {
            HumanCustom.IInputSlider inputSlider = pack.Cast<HumanCustom.IInputSlider>();
            // Slider slider = new HumanCustom.IInputSlider(pack.Pointer).Slider;
            SetSliderRange(inputSlider.Slider);
        }

        private static void SetSliderRange(Slider slider)
        {
            if (slider.maxValue == 1f && slider.minValue == 0f)
            {
                slider.maxValue = Plugin.Maximum.Value / 100f;
                slider.minValue = Plugin.Minimum.Value / 100f;
            }
        }
#endif
    }

    internal static class SliderModCheckHooks
    {
        // optional for clamping, clamp patcher already removes ratio clamping in underlying native code
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HumanDataCheck), nameof(HumanDataCheck.IsFace))]
        [HarmonyPatch(typeof(HumanDataCheck), nameof(HumanDataCheck.IsBody))]
        private static void IsModCheck(bool __result)
        {
            __result = false;
        }
    }

    internal static class AllModCheckHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(
            typeof(HumanData),
            nameof(HumanData.LoadCharaFile),
            [typeof(Il2CppSystem.IO.BinaryReader), typeof(HumanData.LoadFileInfo.Flags)]
        )]
        [HarmonyPatch(typeof(HumanData), nameof(HumanData.CopyLimited))]
        [HarmonyPatch(typeof(HumanData), nameof(HumanData.LoadFromBytes))]
        private static void LoadCharaFile()
        {
            HumanData.IsMod = false;
        }
    }
}
