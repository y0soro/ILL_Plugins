using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AC.CharaFile;
using AC.Scene.Home.UI;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CharacterCreation;
using H.ClothesPanel;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Network.Chara.UI;
using R3;
using UnityEngine;

namespace ILL_CharaFilter;

[BepInProcess("Aicomi")]
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public partial class Plugin : BasePlugin
{
    private static new ManualLogSource Log;

    private static AcCharaFilterCore core;

    private class AcCharaFilterCore(BasePlugin plugin) : CharaFilterManager(plugin)
    {
        protected override void OnUpdate(object id, FilterContextBase context)
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
            else if (id is UPFileListCtrl ctrl2)
            {
                FilterFileList(ctrl2, false);
                Hooks.OrigOnUpdate(ctrl2);
            }
            else if (id is HumanSelectUI)
            {
                var filter = (HumanSelectFilter)context;
                filter.Filter();
            }
            else if (id is CoordePanel)
            {
                var filter = (CoordPanelFilter)context;
                filter.Filter();
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

    // FIXME: make sure to have different type name of registered type in the same namespace,
    //        the keying of type is broken in ClassInjector.RegisterTypeInIl2Cpp
    private static void RegisterIl2cppType<T>()
    {
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(T)))
            ClassInjector.RegisterTypeInIl2Cpp(typeof(T));
    }

    private class CustomFilter : FilterContext<CustomFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(CustomFileInfo item)
        {
            return ItemInfo.FromIllInfo(
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
            return ItemInfo.FromIllInfo(item.FullPath, null, item.IsDefaultData, item.IsMyData);
        }
    }

    private class UploaderFilter : FilterContext<UPFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(UPFileInfo item)
        {
            // prevent user from uploading cards not marked as self-made
            if (!item.IsMyData || item.IsDefaultData)
                return null;
            return ItemInfo.FromIllInfo(
                item.FullPath,
                item.Personality,
                item.IsDefaultData,
                item.IsMyData
            );
        }
    }

    private class StateListener : MonoBehaviour
    {
        private object id;

        internal void Init(object id)
        {
            this.id = id;
        }

        private void OnEnable()
        {
            core.SetFilterContextActive(id, true);
        }

        private void OnDisable()
        {
            core.SetFilterContextActive(id, false);
        }

        private void OnDestroy()
        {
            core.RemoveFilterContext(id);
        }

        internal static StateListener Attach(Component target, object id)
        {
            RegisterIl2cppType<StateListener>();

            var listener = target.gameObject.AddComponent<StateListener>();
            listener.enabled = false;
            listener.Init(id);
            listener.enabled = true;

            return listener;
        }
    }

    private static bool CollectFileList<Info, InfoComp, Filter>(
        FileListUI.ThreadFileListCtrl<Info, InfoComp> __instance,
        bool collectNew,
        out Filter filter
    )
        where Info : FileListUI.ThreadFileInfo
        where InfoComp : FileListUI.ThreadFileInfoComponent
        where Filter : FilterContext<Info>
    {
        if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
        {
            filter = null;
            return false;
        }

        filter = (Filter)filterBase;

        if (collectNew)
        {
            IEnumerable<Info> fileList()
            {
                foreach (var item in __instance.fileList)
                {
                    yield return item;
                }
            }
            filter.CollectNew(fileList());
        }

        return true;
    }

    private static void FilterFileList(CustomFileListCtrl __instance, bool collectNew)
    {
        if (!CollectFileList(__instance, collectNew, out CustomFilter filter))
            return;

        foreach (var info in __instance.fileList)
        {
            info.Visible = filter.FilterIn(info);
        }
    }

    private static void FilterFileList(FusionFileListControl __instance, bool collectNew)
    {
        if (!CollectFileList(__instance, collectNew, out FusionFilter filter))
            return;

        foreach (var info in __instance.fileList)
        {
            info.Visible = filter.FilterIn(info);
        }
    }

    private static void FilterFileList(UPFileListCtrl __instance, bool collectNew)
    {
        if (!CollectFileList(__instance, collectNew, out UploaderFilter filter))
            return;

        foreach (var info in __instance.fileList)
        {
            info.Visible = filter.FilterIn(info);
        }
    }

    private static partial class Hooks
    {
        // capture frame save card fix
        private static CustomFileInfo lastCustomFile = null;

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

        // capture frame save card fix
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CaptureFrame), nameof(CaptureFrame.SaveCard))]
        private static void SaveCard(bool saveNew, ref string fileName)
        {
            // check if this file is our selected card
            if (
                saveNew
                || lastCustomFile == null
                || lastCustomFile.IsDefaultData
                || string.IsNullOrEmpty(fileName)
                || lastCustomFile.FileName != fileName
            )
                return;

            // set filename to the full path so the internal logic of SaveCard
            // saves card to the right file location
            fileName = lastCustomFile.FullPath;
        }

        private static void HookAwakePost(MonoBehaviour instance)
        {
            Log.LogDebug($"FileListCtrl Awake Post {instance}");

            static void SetFilterActive(MonoBehaviour target)
            {
                core.SetFilterContextActive(target, target.isActiveAndEnabled);
            }

            if (instance is CustomFileListCtrl ctrl)
            {
                var filter = new CustomFilter();
                if (!core.AddFilterContext(ctrl, filter))
                    return;

                // core.SetGuiHintPosition(new Vector2(400, 80));
                core.SetGuiHintPosition(new Vector2(1920 - 350 - 350, 80));

                ctrl.OnChangeObservable()
                    .Subscribe(
                        (Il2CppSystem.Action<CustomFileInfo>)(
                            x =>
                            {
                                // capture frame save card fix
                                lastCustomFile = x;

                                filter.SetActiveItem(x);
                                SetFilterActive(instance);
                            }
                        )
                    );
            }
            else if (instance is FusionFileListControl ctrl1)
            {
                var filter = new FusionFilter();
                if (!core.AddFilterContext(ctrl1, filter))
                    return;

                core.SetGuiHintPosition(new Vector2(1920 - 350 - 350, 80));

                ctrl1
                    .OnChangeObservable()
                    .Subscribe(
                        (Il2CppSystem.Action<FusionFileInfo>)
                            delegate(FusionFileInfo x)
                            {
                                filter.SetActiveItem(x);
                                SetFilterActive(instance);
                            }
                    );
            }
            else if (instance is UPFileListCtrl ctrl2)
            {
                var filter = new UploaderFilter();
                if (!core.AddFilterContext(ctrl2, filter))
                    return;

                core.SetGuiHintPosition(new Vector2(1496, 4));

                ctrl2
                    .OnChangeObservable()
                    .Subscribe(
                        (Il2CppSystem.Action<UPFileInfo>)
                            delegate(UPFileInfo x)
                            {
                                filter.SetActiveItem(x);
                                SetFilterActive(instance);
                            }
                    );
            }
            else
            {
                throw null;
            }

            StateListener.Attach(instance, instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CustomFileListCtrl), nameof(CustomFileListCtrl.Awake))]
        [HarmonyPatch(typeof(FusionFileListControl), nameof(FusionFileListControl.Awake))]
        [HarmonyPatch(typeof(UPFileListCtrl), nameof(UPFileListCtrl.Awake))]
        private static void AwakePost(MonoBehaviour __instance)
        {
            try
            {
                HookAwakePost(__instance);
            }
            catch (Exception e)
            {
                Log.LogError(e);
                core.ShowError(e);
            }
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

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(UPFileListCtrl), nameof(UPFileListCtrl.OnUpdate))]
        internal static void OrigOnUpdate(UPFileListCtrl instance)
        {
            throw null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CustomFileListCtrl), nameof(CustomFileListCtrl.OnUpdate))]
        [HarmonyPatch(typeof(FusionFileListControl), nameof(FusionFileListControl.OnUpdate))]
        [HarmonyPatch(typeof(UPFileListCtrl), nameof(UPFileListCtrl.OnUpdate))]
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
                else if (__instance is UPFileListCtrl ctrl2)
                {
                    FilterFileList(ctrl2, true);
                }
            }
            catch (Exception e)
            {
                Log.LogError(e);
                core.ShowError(e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(
            typeof(GameCharaFileInfo.Assist),
            nameof(GameCharaFileInfo.Assist.CreateCharaFileInfoList)
        )]
        private static void CreateCharaFileInfoList(
            Il2CppSystem.Collections.Generic.List<GameCharaFileInfo> list
        )
        {
            if (list != null)
            {
                foreach (var x in list)
                {
                    x.FileName = x.FullPathWithoutExtension;
                    x.FileNameEx = x.FullPath;
                }
            }
        }
    }
}
