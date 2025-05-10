using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Localize.Translate;
using TMPro;
using UnityEngine;

namespace SVS_TMPFontChanger;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    private static new ManualLogSource Log;

    private static TMP_FontAsset Font = null;

    private static bool fontInitialized = false;

    private static ConfigEntry<string> fontPath;

    private static ConfigEntry<string> fallbackFontPath;

    public override void Load()
    {
        Log = base.Log;

        fontPath = Config.Bind(
            "General",
            "OverrideTMPFont",
            "",
            "Path to asset bundle containing TMP_FontAsset for overriding."
        );

        fallbackFontPath = Config.Bind(
            "General",
            "FallbackTMPFont",
            "",
            "Path to asset bundle containing fallback TMP_FontAsset."
        );

        Harmony.CreateAndPatchAll(typeof(Hooks), MyPluginInfo.PLUGIN_GUID);

        SetFallbackFont();
    }

    private static void SetFallbackFont()
    {
        if (fallbackFontPath.Value.IsNullOrWhiteSpace())
            return;

        TMP_FontAsset font = TryGetTextMeshProFont(fallbackFontPath.Value);
        if (font == null)
            return;

        TMP_Settings.fallbackFontAssets.Add(font);

        Log.LogInfo($"Add fallback font {font.name} {font.version}");
    }

    internal static class Hooks
    {
        private static readonly Dictionary<string, Material> _FontMaterialCopies = [];

        // credit: https://github.com/bbepis/XUnity.AutoTranslator/pull/494
        private static bool ChangeFont(TMP_Text tmpText)
        {
            if (!fontInitialized)
            {
                fontInitialized = true;
                if (fontPath.Value.IsNullOrWhiteSpace())
                {
                    Log.LogInfo($"OverrideTMPFont not set, skip font overriding");
                    return false;
                }

                Font = TryGetTextMeshProFont(fontPath.Value);
                if (Font == null)
                    return false;

                Log.LogInfo($"Loaded override font {Font.name} {Font.version}");
            }

            if (Font == null)
                return false;

            if (tmpText.font && tmpText.font.Pointer == Font.Pointer)
                return true;

            var oldFont = tmpText.font;
            var oldMaterial = tmpText.fontSharedMaterial;

            tmpText.font = Font;

            var newMaterial = tmpText.fontSharedMaterial;

            if (oldMaterial != null && newMaterial != null)
            {
                var hashCode = EqualityComparer<Material>.Default.GetHashCode(oldMaterial);
                var key = $"{oldMaterial.name}{hashCode}";

                if (!_FontMaterialCopies.TryGetValue(key, out var copyMaterial))
                {
                    copyMaterial = _FontMaterialCopies[key] = UnityEngine.Object.Instantiate(
                        oldMaterial
                    );
                    UnityEngine.Object.DontDestroyOnLoad(copyMaterial);

                    var uiCopy = UnityEngine.Object.Instantiate(tmpText);
                    uiCopy.font = oldFont;
                    uiCopy.fontSharedMaterial = oldMaterial;

                    copyMaterial.SetTexture("_MainTex", newMaterial.GetTexture("_MainTex"));
                    copyMaterial.SetFloat("_TextureHeight", newMaterial.GetFloat("_TextureHeight"));
                    copyMaterial.SetFloat("_TextureWidth", newMaterial.GetFloat("_TextureWidth"));
                    copyMaterial.SetFloat("_GradientScale", newMaterial.GetFloat("_GradientScale"));
                }

                tmpText.fontSharedMaterial = copyMaterial;
            }

            return true;
        }

        [
            HarmonyPrefix,
            HarmonyPatch(
                typeof(FontProfile),
                nameof(FontProfile.Set),
                [typeof(TMP_Text), typeof(int), typeof(int)]
            )
        ]
        private static bool Set(TMP_Text __0)
        {
            var changed = ChangeFont(__0);
            // skip material loading from FontProfile
            return !changed;
        }

        // for plugins(e.g. SVS_Subtitles) using managed TMP_Text api
        [HarmonyPostfix, HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Setter)]
        private static void SetText(TMP_Text __instance)
        {
            ChangeFont(__instance);
        }
    }

    public static TMP_FontAsset TryGetTextMeshProFont(string assetBundle)
    {
        var fontPath = Path.Join(Application.dataPath, "../", assetBundle);
        if (!File.Exists(fontPath))
            return null;

        try
        {
            var bundle = AssetBundle.LoadFromFile(fontPath);

            var assets = bundle.LoadAllAssets(Il2CppType.From(typeof(TMP_FontAsset)));

            var font = assets?.FirstOrDefault();

            if (font == null)
                return null;

            var fontAsset = font.Cast<TMP_FontAsset>();

            GameObject.DontDestroyOnLoad(fontAsset);

            Log.LogDebug($"Loaded font {fontAsset.name} {fontAsset.version}");

            return fontAsset;
        }
        catch (Exception e)
        {
            Log.LogError($"Failed to load font {fontPath}, {e}");
        }

        return null;
    }
}
