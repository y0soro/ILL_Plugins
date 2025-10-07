using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CharaFilterCore;
using H.ClothesPanel;
using HarmonyLib;
using R3;
using UnityEngine;

namespace AC_CharaFilter;

public partial class Plugin
{
    private class CoordPanelFilter : FilterContext<CmpCoorde.Info>
    {
        private readonly CoordePanel instance;
        private CmpCoorde.Info[] targetInfos = [];

        private CoordPanelFilter(CoordePanel instance)
        {
            this.instance = instance;
        }

        protected override ItemInfo ConvertItemInfo(CmpCoorde.Info item)
        {
            var info = new ItemInfo();
            info.AddIllPngPath(item.Path);
            return info;
        }

        public void CollectNew()
        {
            targetInfos = [.. instance._infoList];
            CollectNew(targetInfos);
        }

        public void Filter()
        {
            var infoArray = targetInfos.Where(FilterIn).ToArray();

            var list = instance._infoList;

            list.Clear();
            foreach (var info in infoArray)
            {
                list.Add(info);
            }

            instance.SelectClear();

            SetActiveItem(null);

            instance.OnUpdateUI();
        }

        public void SetActive(bool active)
        {
            core.SetFilterContextActive(instance, active && instance.isActiveAndEnabled);
        }

        internal static bool TryNew(CoordePanel instance, out CoordPanelFilter filter)
        {
            var filter_ = new CoordPanelFilter(instance);
            if (!core.AddFilterContext(instance, filter_))
            {
                filter = null;
                return false;
            }

            instance._onChange.Subscribe(
                (Il2CppSystem.Action<CmpCoorde.Info>)(
                    info =>
                    {
                        filter_.SetActiveItem(info);
                        filter_.SetActive(true);
                    }
                )
            );

            StateListener.Attach(instance, instance);

            filter = filter_;
            return true;
        }
    }

    private static partial class Hooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CoordePanel), nameof(CoordePanel.Awake))]
        private static void Awake(CoordePanel __instance)
        {
            try
            {
                if (!CoordPanelFilter.TryNew(__instance, out var filter))
                {
                    return;
                }
            }
            catch (Exception e)
            {
                Log.LogError(e);
                core.ShowError(e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CoordePanel), nameof(CoordePanel.OnUpdate))]
        private static void OnUpdate(CoordePanel __instance)
        {
            try
            {
                if (!core.GetFilterContext(__instance, out FilterContextBase filterBase))
                {
                    return;
                }
                var filter = (CoordPanelFilter)filterBase;

                filter.CollectNew();
                filter.Filter();

                core.SetGuiHintPosition(new Vector2(370, 250));
                filter.SetActive(true);
            }
            catch (Exception e)
            {
                Log.LogError(e);
                core.ShowError(e);
            }
        }
    }
}
