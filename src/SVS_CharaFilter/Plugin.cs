using System;
using System.Collections.Concurrent;
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
using SV.H.UI.ClothesSettingMenu;
using UniRx;
using UnityEngine;
using ManagerChaCoordinateInfo = Localize.Translate.Manager.ChaCoordinateInfo;
using SelectChaCoordinateInfo = SV.H.UI.ClothesSettingMenu.SelectCoodinateCard.ChaCoordinateInfo;
#if UPLOADER
using Network.Uploader.Chara;
#endif

namespace SVS_CharaFilter;

[BepInProcess("SamabakeScramble")]
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    private static new ManualLogSource Log;

    private static SvsCharaFilterCore core;

    private class SvsCharaFilterCore(BasePlugin plugin) : CharaFilterManager(plugin)
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
            else if (id is SelectChaCoordinateInfo select)
            {
                var filter = (SelectCoordFilter)context;
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

#if UPLOADER
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
#endif

    private class EntrySceneFilter : FilterContext<EntryFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(EntryFileInfo item)
        {
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

#if UPLOADER
    private static void FilterFileList(UPFileListCtrl __instance, bool collectNew)
    {
        if (!CollectFileList(__instance, collectNew, out UploaderFilter filter))
            return;

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

            if (instance is CustomFileListCtrl ctrl)
            {
                var filter = new CustomFilter();
                if (!core.AddFilterContext(ctrl, filter))
                    return;

                // core.SetGuiHintPosition(new Vector2(400, 80));
                core.SetGuiHintPosition(new Vector2(1920 - 350 - 350, 80));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<CustomFileInfo>)
                        delegate(CustomFileInfo x)
                        {
                            // capture frame save card fix
                            lastCustomFile = x;

                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(instance, true);
                        }
                );
                ctrl._onChange.Subscribe(observer);
            }
            else if (instance is FusionFileListControl ctrl1)
            {
                var filter = new FusionFilter();
                if (!core.AddFilterContext(ctrl1, filter))
                    return;

                core.SetGuiHintPosition(new Vector2(1920 - 350 - 350, 80));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<FusionFileInfo>)
                        delegate(FusionFileInfo x)
                        {
                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(instance, true);
                        }
                );
                ctrl1._onChange.Subscribe(observer);
            }
#if UPLOADER
            else if (instance is UPFileListCtrl ctrl2)
            {
                var filter = new UploaderFilter();
                if (!core.AddFilterContext(ctrl2, filter))
                    return;

                core.SetGuiHintPosition(new Vector2(1420, 4));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<UPFileInfo>)
                        delegate(UPFileInfo x)
                        {
                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(instance, true);
                        }
                );
                ctrl2._onChange.Subscribe(observer);
            }
#endif
            else if (instance is EntryFileListCtrl ctrl3)
            {
                var filter = new EntrySceneFilter();
                if (!core.AddFilterContext(ctrl3, filter))
                    return;

                core.SetGuiHintPosition(new Vector2(1400, 130));

                var observer = Observer.Create(
                    (Il2CppSystem.Action<EntryFileInfo>)
                        delegate(EntryFileInfo x)
                        {
                            filter.SetActiveItem(x);
                            core.SetFilterContextActive(instance, true);
                        }
                );
                ctrl3._onChange.Subscribe(observer);
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
#if UPLOADER
        [HarmonyPatch(typeof(UPFileListCtrl), nameof(UPFileListCtrl.Awake))]
#endif
        [HarmonyPatch(typeof(EntryFileListCtrl), nameof(EntryFileListCtrl.Awake))]
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
                Log.LogError(e);
                core.ShowError(e);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SelectCoodinateCard), nameof(SelectCoodinateCard.Awake))]
        private static void Awake(SelectCoodinateCard __instance)
        {
            SelectCoordFilter.TrackInstance(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SelectChaCoordinateInfo), nameof(SelectChaCoordinateInfo.Load))]
        private static void Load(SelectChaCoordinateInfo __instance, byte __0)
        {
            try
            {
                SelectCoordFilter filter;
                if (core.GetFilterContext(__instance, out FilterContextBase filterBase))
                {
                    filter = (SelectCoordFilter)filterBase;
                }
                else if (!SelectCoordFilter.TryNew(__instance, out filter))
                {
                    return;
                }

                filter.CollectNew();
                filter.Filter();

                core.SetGuiHintPosition(new Vector2(290, 240));
                filter.SetActive(true);
            }
            catch (Exception e)
            {
                Log.LogError(e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SelectChaCoordinateInfo), nameof(SelectChaCoordinateInfo.Sort))]
        private static void Sort(SelectChaCoordinateInfo __instance)
        {
            if (SelectCoordFilter.Sorting)
                return;

            if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
                return;

            var filter = (SelectCoordFilter)filterBase;
            filter.SetActive(true);
        }
    }

    private class SelectCoordFilter : FilterContext<ManagerChaCoordinateInfo>
    {
        private static readonly ConcurrentDictionary<
            SelectCoodinateCard,
            HashSet<SelectCoordFilter>
        > InstanceTracks = [];

        [ThreadStatic]
        private static SelectCoodinateCard lastInstance;

        [ThreadStatic]
        internal static bool Sorting;

        private readonly SelectCoodinateCard selectCoord;
        private readonly SelectChaCoordinateInfo infoSort;

        private ManagerChaCoordinateInfo[] targetInfos = [];

        internal static void TrackInstance(SelectCoodinateCard instance)
        {
            InstanceTracks.TryAdd(instance, []);
            lastInstance = instance;
            SelectCoordStateListener.Attach(instance, instance);
        }

        internal static bool TryNew(SelectChaCoordinateInfo instance, out SelectCoordFilter filter)
        {
            SelectCoodinateCard selectCoord = null;
            foreach (var parent in InstanceTracks.Keys)
            {
                if (parent.useInfos != instance && !parent.coordinateInfos.Contains(instance))
                    continue;

                selectCoord = parent;
                break;
            }

            if (selectCoord == null)
            {
                if (lastInstance == null)
                {
                    filter = null;
                    return false;
                }
                // assume the last is what we want
                selectCoord = lastInstance;
            }

            filter = new(selectCoord, instance);
            InstanceTracks[selectCoord].Add(filter);
            core.AddFilterContext(instance, filter);

            return true;
        }

        private SelectCoordFilter(SelectCoodinateCard selectCoord, SelectChaCoordinateInfo infoSort)
        {
            this.selectCoord = selectCoord;
            this.infoSort = infoSort;
        }

        internal void CollectNew()
        {
            targetInfos = [.. infoSort._info];
            CollectNew(targetInfos);
        }

        internal void Filter()
        {
            var infoArray = targetInfos.Where(FilterIn).ToArray();
            infoSort._info = infoArray;
            infoSort.InfoLength = infoArray.Length;

            infoSort._infoIdxes.Clear();
            for (var i = 0; i < infoArray.Length; i++)
            {
                infoSort._infoIdxes.Add(i);
            }

            Sorting = true;
            // this redraws list, calling twice to preserve the original order
            selectCoord._sortOrder._button.Press();
            selectCoord._sortOrder._button.Press();
            Sorting = false;
        }

        internal void SetActive(bool active)
        {
            core.SetFilterContextActive(infoSort, active && selectCoord.isActiveAndEnabled);
        }

        protected override ItemInfo ConvertItemInfo(ManagerChaCoordinateInfo item)
        {
            return ItemInfo.FromIllInfo(item.info.FullPath, null, item.isDefault, false);
        }

        private class SelectCoordStateListener : MonoBehaviour
        {
            private SelectCoodinateCard selectCoord = null;

            private void SetActive(bool active)
            {
                if (selectCoord == null)
                    return;
                if (
                    !InstanceTracks.TryGetValue(selectCoord, out HashSet<SelectCoordFilter> filters)
                )
                    return;

                foreach (var filter in filters)
                {
                    filter.SetActive(active);
                }
            }

            private void OnEnable()
            {
                SetActive(true);
            }

            private void OnDisable()
            {
                SetActive(false);
            }

            private void OnDestroy()
            {
                if (selectCoord == null)
                    return;
                if (!InstanceTracks.Remove(selectCoord, out HashSet<SelectCoordFilter> filters))
                    return;

                foreach (var filter in filters)
                {
                    core.RemoveFilterContext(filter.infoSort);
                }
            }

            internal static SelectCoordStateListener Attach(
                Component target,
                SelectCoodinateCard selectCoord
            )
            {
                RegisterIl2cppType<SelectCoordStateListener>();

                var listener = target.gameObject.AddComponent<SelectCoordStateListener>();
                listener.enabled = false;
                listener.selectCoord = selectCoord;
                listener.enabled = true;

                return listener;
            }
        }
    }
}
