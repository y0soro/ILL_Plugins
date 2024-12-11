using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CharacterCreation;
using CharaFilterCore;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SV.EntryScene;
using UniRx;
using UnityEngine;
#if UPLOADER
using Network.Uploader.Chara;
#endif

namespace SVS_CharaFilter;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    private static new ManualLogSource Log;

    private static SvsCharaFilterCore core;

    private class SvsCharaFilterCore(BasePlugin plugin) : CharaFilterManager(plugin)
    {
        protected override void OnUpdate(MonoBehaviour id, FilterContextBase context)
        {
            if (id is CustomFileListCtrl ctrl)
            {
                FilterFileList(ctrl, false);
                Hooks.OrigOnUpdate(ctrl);
            }
            else if (id is FusionFileListControl ctrl1)
            {
                FilterFileList(ctrl1, false);
                Hooks.OrigOnUpdate(ctrl1);
            }
#if UPLOADER
            else if (id is UPFileListCtrl ctrl2)
            {
                FilterFileList(ctrl2, false);
                Hooks.OrigOnUpdate(ctrl2);
            }
#endif
            else if (id is EntryFileListCtrl ctrl3)
            {
                FilterFileList(ctrl3, false);
                Hooks.OrigOnUpdate(ctrl3);
            }
            else
            {
                throw null;
            }
        }
    }

    public override void Load()
    {
        Log = base.Log;

        core = new(this);

        Harmony.CreateAndPatchAll(typeof(Hooks), MyPluginInfo.PLUGIN_GUID);
    }

    private static void RegisterIl2cppType<T>()
    {
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(T)))
            ClassInjector.RegisterTypeInIl2Cpp(typeof(T));
    }

    private class CustomFilter : FilterContext<CustomFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(CustomFileInfo item)
        {
            return ItemInfo.FromIllInfo<CustomFileInfo>(
                item.FullPath,
                item.Personality,
                item.IsDefaultData,
                item.IsMyData
            );
        }
    }

    private class FusionFilter : FilterContext<FusionFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(FusionFileInfo item)
        {
            return ItemInfo.FromIllInfo<CustomFileInfo>(
                item.FullPath,
                null,
                item.IsDefaultData,
                item.IsMyData
            );
        }
    }

#if UPLOADER
    private class UploaderFilter : FilterContext<UPFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(UPFileInfo item)
        {
            // prevent user from uploading cards not marked as self-made
            if (!item.IsMyData || item.IsDefaultData)
                return null;
            return ItemInfo.FromIllInfo<UPFileInfo>(
                item.FullPath,
                item.Personality,
                item.IsDefaultData,
                item.IsMyData
            );
        }
    }
#endif

    private class EntrySceneFilter : FilterContext<EntryFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(EntryFileInfo item)
        {
            return ItemInfo.FromIllInfo<EntryFileInfo>(
                item.FullPath,
                item.Personality,
                item.IsDefaultData,
                item.IsMyData
            );
        }
    }

    private class StateListener : MonoBehaviour
    {
        private MonoBehaviour targetInstance;

        internal void Init(MonoBehaviour targetInstance_)
        {
            targetInstance = targetInstance_;
            core.SetFilterContextActive(targetInstance, true);
        }

        private void OnEnable()
        {
            core.SetFilterContextActive(targetInstance, true);
        }

        private void OnDisable()
        {
            core.SetFilterContextActive(targetInstance, false);
        }

        private void OnDestroy()
        {
            core.RemoveFilterContext(targetInstance);
        }
    }

    private static void FilterFileList(CustomFileListCtrl __instance, bool collectNew)
    {
        if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
            return;

        var filter = (CustomFilter)filterBase;

        if (collectNew)
        {
            IEnumerable<CustomFileInfo> fileList()
            {
                foreach (var item in __instance.fileList)
                {
                    yield return item;
                }
            }
            filter.CollectNew(fileList());
        }

        foreach (var info in __instance.fileList)
        {
            info.Visible = filter.FilterIn(info);
        }
    }

    private static void FilterFileList(FusionFileListControl __instance, bool collectNew)
    {
        if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
            return;

        var filter = (FusionFilter)filterBase;

        if (collectNew)
        {
            IEnumerable<FusionFileInfo> fileList()
            {
                foreach (var item in __instance.fileList)
                {
                    yield return item;
                }
            }
            filter.CollectNew(fileList());
        }

        foreach (var info in __instance.fileList)
        {
            info.Visible = filter.FilterIn(info);
        }
    }

#if UPLOADER
    private static void FilterFileList(UPFileListCtrl __instance, bool collectNew)
    {
        if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
            return;

        var filter = (UploaderFilter)filterBase;

        if (collectNew)
        {
            IEnumerable<UPFileInfo> fileList()
            {
                foreach (var item in __instance.fileList)
                {
                    yield return item;
                }
            }
            filter.CollectNew(fileList());
        }

        foreach (var info in __instance.fileList)
        {
            info.Visible = filter.FilterIn(info);
        }
    }
#endif

    internal static void FilterFileList(EntryFileListCtrl __instance, bool collectNew)
    {
        if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
            return;

        var filter = (EntrySceneFilter)filterBase;

        if (collectNew)
        {
            IEnumerable<EntryFileInfo> fileList()
            {
                foreach (var item in __instance._fileList)
                {
                    yield return item;
                }
            }
            filter.CollectNew(fileList());
        }

        foreach (var info in __instance._fileList)
        {
            info.Visible = filter.FilterIn(info);
        }
    }

    private static class Hooks
    {
        private static readonly string[] recursiveDirs =
        [
            "UserData\\chara",
            "UserData\\coordinate",
        ];

        [HarmonyPrefix]
        [HarmonyPatch(
            typeof(Il2CppSystem.IO.Directory),
            nameof(Il2CppSystem.IO.Directory.GetFiles),
            [typeof(string), typeof(string)]
        )]
        private static bool GetFiles(string __0, string __1, ref Il2CppStringArray __result)
        {
            var dir = Path.GetDirectoryName(
                Path.GetRelativePath(Path.Join(Application.dataPath, "../"), __0)
            );

            Log.LogDebug($"GetFiles {dir} pattern {__1}");

            if (!recursiveDirs.Any(dir.StartsWith))
                return true;

            __result = Directory.GetFiles(__0, __1, SearchOption.AllDirectories);

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CustomFileListCtrl), nameof(CustomFileListCtrl.Awake))]
        [HarmonyPatch(typeof(FusionFileListControl), nameof(FusionFileListControl.Awake))]
#if UPLOADER
        [HarmonyPatch(typeof(UPFileListCtrl), nameof(UPFileListCtrl.Awake))]
#endif
        [HarmonyPatch(typeof(EntryFileListCtrl), nameof(EntryFileListCtrl.Awake))]
        private static void AwakePost(MonoBehaviour __instance)
        {
            Log.LogDebug($"FileListCtrl Awake Post {__instance}");

            if (__instance is CustomFileListCtrl ctrl)
            {
                var filter = new CustomFilter();
                core.AddFilterContext(ctrl, filter);

                // core.SetGuiHintPosition(new Vector2(400, 80));
                core.SetGuiHintPosition(new Vector2(1920 - 350 - 350, 80));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<CustomFileInfo>)
                        delegate(CustomFileInfo x)
                        {
                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(__instance, true);
                        }
                );
                ctrl._onChange.Subscribe(observer);
            }
            else if (__instance is FusionFileListControl ctrl1)
            {
                var filter = new FusionFilter();
                core.AddFilterContext(ctrl1, filter);

                core.SetGuiHintPosition(new Vector2(1920 - 350 - 350, 80));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<FusionFileInfo>)
                        delegate(FusionFileInfo x)
                        {
                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(__instance, true);
                        }
                );
                ctrl1._onChange.Subscribe(observer);
            }
#if UPLOADER
            else if (__instance is UPFileListCtrl ctrl2)
            {
                var filter = new UploaderFilter();
                core.AddFilterContext(ctrl2, filter);

                core.SetGuiHintPosition(new Vector2(1420, 4));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<UPFileInfo>)
                        delegate(UPFileInfo x)
                        {
                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(__instance, true);
                        }
                );
                ctrl2._onChange.Subscribe(observer);
            }
#endif
            else if (__instance is EntryFileListCtrl ctrl3)
            {
                var filter = new EntrySceneFilter();
                core.AddFilterContext(ctrl3, filter);

                core.SetGuiHintPosition(new Vector2(350, 126));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<EntryFileInfo>)
                        delegate(EntryFileInfo x)
                        {
                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(__instance, true);
                        }
                );
                ctrl3._onChange.Subscribe(observer);
            }
            else
            {
                throw null;
            }

            RegisterIl2cppType<StateListener>();
            var listener = __instance.gameObject.AddComponent<StateListener>();
            listener.Init(__instance);
        }

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(CustomFileListCtrl), nameof(CustomFileListCtrl.OnUpdate))]
        internal static void OrigOnUpdate(CustomFileListCtrl instance)
        {
            throw null;
        }

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(FusionFileListControl), nameof(FusionFileListControl.OnUpdate))]
        internal static void OrigOnUpdate(FusionFileListControl instance)
        {
            throw null;
        }

#if UPLOADER
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(UPFileListCtrl), nameof(UPFileListCtrl.OnUpdate))]
        internal static void OrigOnUpdate(UPFileListCtrl instance)
        {
            throw null;
        }
#endif

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(EntryFileListCtrl), nameof(EntryFileListCtrl.OnUpdate))]
        internal static void OrigOnUpdate(EntryFileListCtrl instance)
        {
            throw null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CustomFileListCtrl), nameof(CustomFileListCtrl.OnUpdate))]
        [HarmonyPatch(typeof(FusionFileListControl), nameof(FusionFileListControl.OnUpdate))]
#if UPLOADER
        [HarmonyPatch(typeof(UPFileListCtrl), nameof(UPFileListCtrl.OnUpdate))]
#endif
        [HarmonyPatch(typeof(EntryFileListCtrl), nameof(EntryFileListCtrl.OnUpdate))]
        private static void OnUpdate(MonoBehaviour __instance)
        {
            Log.LogDebug($"FileListCtrl OnUpdate {__instance}");

            try
            {
                if (__instance is CustomFileListCtrl ctrl)
                {
                    FilterFileList(ctrl, true);
                }
                else if (__instance is FusionFileListControl ctrl1)
                {
                    FilterFileList(ctrl1, true);
                }
#if UPLOADER
                else if (__instance is UPFileListCtrl ctrl2)
                {
                    FilterFileList(ctrl2, true);
                }
#endif
                else if (__instance is EntryFileListCtrl ctrl3)
                {
                    FilterFileList(ctrl3, true);
                }
            }
            catch (Exception e)
            {
                // XXX: pass error to filter UI
                Log.LogError(e);
            }
        }
    }
}
