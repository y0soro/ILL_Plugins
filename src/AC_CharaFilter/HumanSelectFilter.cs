using System;
using System.Linq;
using AC.CharaFile;
using AC.Scene.Home.UI;
using CharaFilterCore;
using HarmonyLib;
using R3;
using UnityEngine;

namespace AC_CharaFilter;

public partial class Plugin
{
    private class HumanSelectFilter : FilterContext<GameCharaFileInfo>
    {
        private readonly HumanSelectUI instance;
        private byte currSex = 0;
        private GameCharaFileInfo[] targetInfos = [];

        private HumanSelectFilter(HumanSelectUI instance)
        {
            this.instance = instance;
        }

        protected override ItemInfo ConvertItemInfo(GameCharaFileInfo item)
        {
            return ItemInfo.FromIllInfo(
                item.FullPath,
                item.Personality,
                item.CateKind.HasFlag(CategoryKind.Preset),
                item.CateKind.HasFlag(CategoryKind.MyData)
            );
        }

        public void CollectNew()
        {
            currSex = instance._sex.CurrentValue;
            targetInfos = [.. instance._charaList[currSex]];
            CollectNew(targetInfos);
        }

        public void Filter()
        {
            var infoArray = targetInfos.Where(FilterIn).ToArray();

            var list = instance._charaList[currSex];

            list.Clear();
            foreach (var info in infoArray)
            {
                list.Add(info);
            }

            if (instance._listCtrls[currSex]._selectInfo != null)
            {
                instance._listCtrls[currSex].ClearSelectInfo();
            }

            SetActiveItem(null);

            HumanSelectUI.Sort(list, instance.SortKind, instance.SortOrder);
            instance.RedrawListView();
        }

        public void SetActive(bool active)
        {
            core.SetFilterContextActive(instance, active && instance.isActiveAndEnabled);
        }

        internal static bool TryNew(HumanSelectUI instance, out HumanSelectFilter filter)
        {
            var filter_ = new HumanSelectFilter(instance);
            if (!core.AddFilterContext(instance, filter_))
            {
                filter = null;
                return false;
            }

            instance._sex.Subscribe(
                (Il2CppSystem.Action<byte>)(
                    sex =>
                    {
                        filter_.CollectNew();
                        filter_.SetActive(true);
                    }
                )
            );

            // XXX: where is deselect hook?
            instance
                .OnSelectAsObservable()
                .Subscribe(
                    (Il2CppSystem.Action<GameCharaFileInfo>)(
                        info =>
                        {
                            filter_.SetActiveItem(info);
                            filter_.SetActive(true);
                        }
                    )
                );

            filter = filter_;
            return true;
        }
    }

    private static partial class Hooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HumanSelectUI), nameof(HumanSelectUI.Open))]
        private static void Open(HumanSelectUI __instance)
        {
            try
            {
                HumanSelectFilter filter;
                if (core.GetFilterContext(__instance, out FilterContextBase filterBase))
                {
                    filter = (HumanSelectFilter)filterBase;
                }
                else if (HumanSelectFilter.TryNew(__instance, out filter))
                {
                    core.SetGuiHintPosition(new Vector2(176, 480));
                    filter.CollectNew();
                    filter.Filter();
                }
                else
                {
                    return;
                }

                filter.SetActive(true);
            }
            catch (Exception e)
            {
                Log.LogError(e);
                core.ShowError(e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HumanSelectUI), nameof(HumanSelectUI.CreateList))]
        private static void CreateList(HumanSelectUI __instance)
        {
            try
            {
                if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
                {
                    return;
                }

                var filter = (HumanSelectFilter)filterBase;

                filter.CollectNew();
                filter.Filter();
            }
            catch (Exception e)
            {
                Log.LogError(e);
                core.ShowError(e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HumanSelectUI), nameof(HumanSelectUI.Close))]
        private static void Close(HumanSelectUI __instance)
        {
            if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
            {
                return;
            }

            var filter = (HumanSelectFilter)filterBase;

            filter.SetActive(false);
        }
    }
}
