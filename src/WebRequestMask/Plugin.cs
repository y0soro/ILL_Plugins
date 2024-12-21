using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine.Networking;
using WebRequestMask.Core;

namespace WebRequestMask;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    private static new ManualLogSource Log;
    private static CommonPlugin core;

    public override void Load()
    {
        Log = base.Log;
        core = new(Log, Config);

        Hooks.core = core;
        Harmony.CreateAndPatchAll(typeof(Hooks), MyPluginInfo.PLUGIN_GUID);
    }

    internal static class Hooks
    {
        internal static CommonPlugin core;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UnityWebRequest), nameof(UnityWebRequest.SendWebRequest))]
        private static void SendWebRequest(UnityWebRequest __instance)
        {
            if (core.urlProxy.HasHttpProxy() || core.MaskUrl(__instance.url))
            {
                Log.LogDebug($"Redirect {__instance.url}");
                __instance.url = core.urlProxy.ProxyUrl(__instance.url);
            }
        }
    }
}
