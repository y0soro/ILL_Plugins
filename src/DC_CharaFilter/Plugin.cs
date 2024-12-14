using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CharaFilterCore;
using DigitalCraft;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UniRx;
using UnityEngine;

namespace DC_CharaFilter;

[BepInProcess("DigitalCraft")]
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    private static new ManualLogSource Log;

    private static DcCharaFilterCore core;

    private static readonly ConcurrentDictionary<CharaFileSort, object> idMap = [];

    public override void Load()
    {
        Log = base.Log;

        core = new(this);

        Harmony.CreateAndPatchAll(typeof(DcHooks), MyPluginInfo.PLUGIN_GUID);
    }

    private class DcCharaFilterCore(BasePlugin plugin) : CharaFilterManager(plugin)
    {
        protected override void OnUpdate(object id, FilterContextBase context)
        {
            FilterList(id, (CharaFileSortFilter)context);
        }
    }

    private class CharaFileSortFilter : FilterContext<CharaFileInfo>
    {
        protected override ItemInfo ConvertItemInfo(CharaFileInfo item)
        {
            ItemInfo info = new();
            info.AddIllPngPath(item.file);

            string gType = L10n.Group("Type");
            info.AddGroup(gType, -100, true);

            string tType;
            if (item.Kind == 1)
            {
                tType = L10n.Tag("HC");
            }
            else if (item.Kind == 2)
            {
                tType = L10n.Tag("SV");
            }
            else
            {
                tType = item.Kind.ToString();
            }

            info.AddTag(tType, gType, item.Kind);

            return info;
        }
    }

    private static void FilterList(object id, CharaFileSortFilter filter)
    {
        int DoFilter(CharaFileSort fileSort)
        {
            fileSort._charaFileInfos.Clear();

            foreach (var item in fileSort.cfiList)
            {
                if (filter.FilterIn(item))
                {
                    fileSort._charaFileInfos.Add(item);
                }
            }

            return fileSort.sortKind;
        }

        if (id is CharaList charaList)
        {
            var sort = DoFilter(charaList.charaFileSort);

            // sort would also re-renders list
            charaList.OnSort(sort);
            charaList.OnSort(sort);
        }
        else if (id is MPCharCtrl.CostumeInfo costumeInfo)
        {
            var sort = DoFilter(costumeInfo.fileSort);

            // sort would also re-renders list
            costumeInfo.OnClickSort(sort);
            costumeInfo.OnClickSort(sort);
        }
        else
        {
            throw null;
        }
    }

    private static bool GetFilter(
        CharaFileSort __instance,
        out object id,
        out CharaFileSortFilter filter
    )
    {
        if (
            !idMap.TryGetValue(__instance, out id)
            || !core.GetFilterContext(id, out FilterContextBase filterBase)
        )
        {
            filter = null;
            return false;
        }

        filter = (CharaFileSortFilter)filterBase;
        return true;
    }

    private static void SetSelected(CharaFileSort fileSort)
    {
        if (!GetFilter(fileSort, out _, out CharaFileSortFilter filter))
            return;

        filter.SetActiveItem(fileSort[fileSort.select]);
    }

    private static void RegisterIl2cppType<T>()
    {
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(T)))
            ClassInjector.RegisterTypeInIl2Cpp(typeof(T));
    }

    private class StateListener : MonoBehaviour
    {
        private object id = null;

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
            foreach (var kv in idMap.Where(kv => kv.Value == id).ToList())
            {
                idMap.Remove(kv.Key, out _);
            }
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

    private static class DcHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharaFileSort), nameof(CharaFileSort.SetList))]
        private static void SetList(CharaFileSort __instance)
        {
            Log.LogDebug($"SetList {__instance}");

            try
            {
                if (!GetFilter(__instance, out object id, out CharaFileSortFilter filter))
                    return;

                IEnumerable<CharaFileInfo> fileList()
                {
                    foreach (var item in __instance.cfiList)
                    {
                        yield return item;
                    }
                }
                filter.CollectNew(fileList());

                FilterList(id, filter);
            }
            catch (Exception e)
            {
                Log.LogError(e);
                core.ShowError(e);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CharaFileSort), nameof(CharaFileSort.Filter))]
        private static bool Filter(CharaFileSort __instance)
        {
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CharaList), nameof(CharaList.Start))]
        private static void Start(CharaList __instance)
        {
            Log.LogDebug($"Start {__instance}");

            var filter = new CharaFileSortFilter();
            var id = __instance;
            if (!core.AddFilterContext(id, filter))
                return;

            idMap.TryAdd(__instance.charaFileSort, id);

            core.SetGuiHintPosition(new Vector2(374, 410));

            StateListener.Attach(__instance, id);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MPCharCtrl.CostumeInfo), nameof(MPCharCtrl.CostumeInfo.Init))]
        private static void Init(MPCharCtrl.CostumeInfo __instance, MPCharCtrl __0)
        {
            Log.LogDebug($"Init {__instance}");

            var filter = new CharaFileSortFilter();
            var id = __instance;
            if (!core.AddFilterContext(id, filter))
                return;

            idMap.TryAdd(__instance.fileSort, id);

            core.SetGuiHintPosition(new Vector2(374, 410));

            var attachPoint = (Component)__0.transform.Find("05_Costume") ?? __0;
            StateListener.Attach(attachPoint, id);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharaList), nameof(CharaList.OnSelectChara))]
        private static void OnSelectChara(CharaList __instance)
        {
            SetSelected(__instance.charaFileSort);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MPCharCtrl.CostumeInfo), nameof(MPCharCtrl.CostumeInfo.OnSelect))]
        private static void OnSelect(MPCharCtrl.CostumeInfo __instance)
        {
            SetSelected(__instance.fileSort);
        }
    }
}
