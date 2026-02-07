using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace ILL_CharaFilter;

public abstract class CharaFilterManager
{
    internal static ManualLogSource Log;

    private readonly ConfigEntry<KeyboardShortcut> toggleShortcut;
    private readonly ConfigEntry<bool> autoOpen;
    private readonly ConfigEntry<bool> isShowError;

    private readonly UpdateListener updateListener;
    private readonly FilterUI globalFilterUI;

    private readonly ConcurrentDictionary<object, FilterWrapper> filterMap = [];
    private readonly List<object> cacheActiveFilterIds = [];

    private readonly object lockObj = new();
    private object currTarget = null;

    private Vector2? guiHintPosition = null;

    private readonly Queue<string> errors = [];

    private class FilterWrapper
    {
        internal readonly FilterContextBase filter;

        internal bool active = false;

        internal FilterWrapper(FilterContextBase filter)
        {
            this.filter = filter;
        }
    }

    public CharaFilterManager(BasePlugin plugin)
    {
        Log = plugin.Log;
        FilterContextBase.Log = Log;

        autoOpen = plugin.Config.Bind(
            "General",
            "Auto Open",
            true,
            "Auto popup filter UI if there is a card list."
        );

        toggleShortcut = plugin.Config.Bind(
            "General",
            "Toggle Filter UI",
            new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl),
            "Shortcut key to toggle filter UI."
        );

        var lang = plugin
            .Config.Bind(
                "General",
                "Language",
                "",
                "Translation language used, auto-detect if empty, one of en, ja-JP, zh-CN or zh-TW. Needs restart to take effect."
            )
            .Value;

        isShowError = plugin.Config.Bind(
            "General",
            "Show Error",
            true,
            "Show errors captured during filtering in filter UI."
        );

        // IMPORTANT: init l10n before init UI classes
        L10n.Init(lang);

        updateListener = plugin.AddComponent<UpdateListener>();
        updateListener.core = this;
        updateListener.enabled = true;

        globalFilterUI = plugin.AddComponent<FilterUI>();
        globalFilterUI.enabled = false;
        globalFilterUI.core = this;
    }

    public bool AddFilterContext(object id, FilterContextBase context)
    {
        lock (lockObj)
        {
            return filterMap.TryAdd(id, new FilterWrapper(context));
        }
    }

    public bool GetFilterContext(object id, out FilterContextBase context)
    {
        if (filterMap.TryGetValue(id, out FilterWrapper wrapper))
        {
            context = wrapper.filter;
            return true;
        }

        context = null;
        return false;
    }

    public bool RemoveFilterContext(object id)
    {
        lock (lockObj)
        {
            if (filterMap.TryRemove(id, out FilterWrapper wrapper))
            {
                if (wrapper.active)
                {
                    cacheActiveFilterIds.Remove(id);
                }
                return true;
            }
        }
        return false;
    }

    public void SetGuiHintPosition(Vector2 hint)
    {
        guiHintPosition = hint;
    }

    public void ShowError(Exception error)
    {
        if (errors.Count > 10)
        {
            errors.Dequeue();
        }
        errors.Enqueue(error.ToString());
    }

    public void SetFilterContextActive(object id, bool active)
    {
        if (id == null)
            return;
        lock (lockObj)
        {
            var filter = filterMap[id];
            var prevActive = filter.active;
            filter.active = active;
            if (active)
            {
                currTarget = id;
            }
            if (prevActive == active)
            {
                return;
            }

            if (active)
            {
                cacheActiveFilterIds.Add(id);
            }
            else
            {
                cacheActiveFilterIds.Remove(id);
            }

            if (cacheActiveFilterIds.Count == 0)
            {
                globalFilterUI.enabled = false;
                currTarget = null;
            }
            else if (autoOpen.Value)
            {
                globalFilterUI.enabled = true;
            }
        }
    }

    protected abstract void OnUpdate(object id, FilterContextBase context);

    private class KeyListener : MonoBehaviour
    {
        internal CharaFilterManager core = null;
    }

    private class UpdateListener : MonoBehaviour
    {
        internal CharaFilterManager core = null;

        private void Update()
        {
            if (core == null)
                return;

            if (core.currTarget != null && core.toggleShortcut.Value.IsDown())
            {
                core.globalFilterUI.enabled = !core.globalFilterUI.enabled;
            }
        }
    }

    private class FilterUI : MonoBehaviour
    {
        internal CharaFilterManager core = null;

        private static readonly int windowID = 1733398802;

        private Rect windowSize = new(0, 0, 350, 600);

        private Rect windowRect = new(1920 - 690, 84, 350, 600);
        private Vector2 scrollPos = new(0, 0);

        private static GUISkin customSkin = null;
        private static GUIStyle inactiveBtnStyle = null;
        private static GUIStyle disabledBtnStyle = null;

        private static GUIStyle excludedToggleStyle = null;

        private static GUIStyle titleStyle = null;

        internal bool uiMultiSelect = false;
        private bool uiExcludeSelect = false;

        internal string sourceGroupName = L10n.Group("Source");
        internal string textWindowTitle = L10n.UI("Chara Filter");
        internal string textReset = L10n.UI("Reset");
        internal string textPopulate = L10n.UI("Populate");
        internal string textExcludeSelect = L10n.UI("Exclude-select");
        internal string textMultiSelect = L10n.UI("Multi-select");
        internal string textAll = L10n.UI("All");
        internal string textAnd = L10n.UI("AND");
        internal string textOr = L10n.UI("OR");
        internal string textList_format = L10n.UI("List #{0}");

        private static void EnsureCustomStyle()
        {
            if (customSkin != null)
                return;

            var skin = Instantiate(GUI.skin);
            DontDestroyOnLoad(skin);

            var windowBg = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            windowBg.LoadImage(Utils.FindEmbeddedResource("guisharp-window.png"), false);
            DontDestroyOnLoad(windowBg);

            skin.window.onNormal.background = null;
            skin.window.normal.background = windowBg;
            skin.window.normal.textColor = Color.white;
            skin.window.padding.Set(8, 8, 24, 8);
            skin.window.border.Set(10, 10, 21, 10);

            var boxBg = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            boxBg.LoadImage(Utils.FindEmbeddedResource("guisharp-box.png"), false);
            DontDestroyOnLoad(boxBg);
            skin.box.onNormal.background = null;
            skin.box.normal.background = boxBg;
            skin.box.normal.textColor = Color.white;

            var includedColor = new Color(64 / 255f, 192 / 255f, 64 / 255f, 1);
            skin.toggle.onNormal.textColor = includedColor;
            skin.toggle.onHover.textColor = includedColor;
            skin.toggle.onActive.textColor = includedColor;

            customSkin = skin;

            var inactiveColor = new Color(0.7f, 0.7f, 0.7f, 1);

            inactiveBtnStyle = GUI.skin.button.CreateCopy();
            inactiveBtnStyle.normal.textColor = inactiveColor;

            disabledBtnStyle = inactiveBtnStyle.CreateCopy();
            var normalBg = disabledBtnStyle.normal.background;
            disabledBtnStyle.hover.background = normalBg;
            disabledBtnStyle.focused.background = normalBg;
            disabledBtnStyle.active.background = normalBg;
            disabledBtnStyle.hover.textColor = inactiveColor;
            disabledBtnStyle.focused.textColor = inactiveColor;
            disabledBtnStyle.active.textColor = inactiveColor;

            titleStyle = GUI.skin.label.CreateCopy();
            titleStyle.fontSize = 13;
            titleStyle.stretchWidth = true;
            titleStyle.wordWrap = false;
            titleStyle.alignment = TextAnchor.MiddleLeft;

            var excludedColor = new Color(172 / 255f, 64 / 255f, 64 / 255f, 1);
            excludedToggleStyle = GUI.skin.toggle.CreateCopy();
            excludedToggleStyle.onNormal.textColor = excludedColor;
            excludedToggleStyle.onActive.textColor = excludedColor;
            excludedToggleStyle.onHover.textColor = excludedColor;
        }

        private void DrawListSelector()
        {
            if (core.cacheActiveFilterIds.Count <= 1)
                return;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < core.cacheActiveFilterIds.Count; i++)
            {
                var id = core.cacheActiveFilterIds[i];
                GUIStyle style = id == core.currTarget ? GUI.skin.button : inactiveBtnStyle;

                if (GUILayout.Button(string.Format(textList_format, i), style))
                {
                    core.SetFilterContextActive(id, true);
                }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawGlobalControl(FilterContextBase filter, ref bool needUpdate)
        {
            GUILayout.BeginHorizontal();

            var prevUiExcludeSelect = uiExcludeSelect;
            uiExcludeSelect = GUILayout.Toggle(
                prevUiExcludeSelect,
                textExcludeSelect,
                excludedToggleStyle
            );
            if (prevUiExcludeSelect != uiMultiSelect && uiExcludeSelect)
            {
                uiMultiSelect = true;
            }

            var prevMultiSelect = uiMultiSelect;
            uiMultiSelect = GUILayout.Toggle(prevMultiSelect, textMultiSelect);

            if (prevMultiSelect != uiMultiSelect && !uiMultiSelect)
            {
                filter.ResetAll();
                needUpdate = true;
                uiExcludeSelect = false;
            }

            if (GUILayout.Button(textReset))
            {
                filter.ResetAll();
                uiExcludeSelect = false;
                uiMultiSelect = false;
                needUpdate = true;
            }

            if (filter.activeItemInfo != null)
            {
                if (GUILayout.Button(textPopulate))
                {
                    filter.PopulateActiveItemState();
                    uiMultiSelect = true;
                    needUpdate = true;
                }
            }
            else
            {
                GUILayout.Button(textPopulate, disabledBtnStyle);
            }

            GUILayout.EndHorizontal();

#if DEBUG
            GUILayout.Label(windowRect.ToString());
#endif
        }

        private bool DrawGroupControl(
            FilterContextBase.GroupFilterState groupState,
            ref bool needUpdate
        )
        {
            GUILayout.BeginHorizontal(GUI.skin.box);

            var titleText = groupState.uiCollapsed ? groupState.group + " …" : groupState.group;
            if (GUILayout.Button(titleText, titleStyle, GUILayout.ExpandWidth(true)))
            {
                groupState.uiCollapsed = !groupState.uiCollapsed;
            }

            GUILayout.Label(groupState.uiCollapsed ? "+" : "-", titleStyle, GUILayout.Width(10));

            GUILayout.EndHorizontal();

            if (groupState.uiCollapsed)
            {
                return false;
            }

            GUILayout.BeginHorizontal();

            bool isAll = GUILayout.Toggle(groupState.isInactive, textAll, GUILayout.MinHeight(22));
            if (isAll && !groupState.isInactive)
            {
                groupState.Reset();
                needUpdate = true;
            }

            GUILayout.FlexibleSpace();

            if (uiMultiSelect)
            {
                var modeText = groupState.isFilterByAnd ? textAnd : textOr;
                if (GUILayout.Button(modeText, GUILayout.MinWidth(40)))
                {
                    groupState.isFilterByAnd = !groupState.isFilterByAnd;
                    if (groupState.isFilterByAnd)
                    {
                        groupState.Reset();
                        if (groupState.AllowMultiSelect)
                        {
                            uiMultiSelect = true;
                        }
                    }
                    needUpdate = true;
                }
            }
            GUILayout.EndHorizontal();

            return true;
        }

        private void DrawTag(
            FilterContextBase filter,
            FilterContextBase.GroupFilterState groupState,
            FilterContextBase.TagState tagState,
            ref bool groupNeedUpdate,
            ref bool needUpdate
        )
        {
            var isFilterOut = tagState.op == FilterContextBase.TagMatchOp.FilterOut;
            var prev = tagState.op != FilterContextBase.TagMatchOp.Inactive;
            var curr = GUILayout.Toggle(
                prev,
                tagState.tag,
                isFilterOut ? excludedToggleStyle : GUI.skin.toggle,
                GUILayout.Width(98)
            );
            if (prev == curr)
                return;

            if (uiMultiSelect && groupState.AllowMultiSelect)
            {
                groupNeedUpdate = true;
            }
            else
            {
                // clear others
                groupState.Reset();
                groupState.isInactive = !curr;
                needUpdate = true;
            }

            if (curr && uiExcludeSelect)
            {
                if (groupState.isInactive)
                {
                    groupState.Reset(false, FilterContextBase.TagMatchOp.FilterIn);
                }
                tagState.op = FilterContextBase.TagMatchOp.FilterOut;
                // uiExcludeSelect = false;
            }
            else
            {
                tagState.op = curr
                    ? FilterContextBase.TagMatchOp.FilterIn
                    : FilterContextBase.TagMatchOp.Inactive;
            }
        }

        private void DrawGroup(
            int cols,
            FilterContextBase filter,
            FilterContextBase.GroupFilterState groupState,
            ref bool needUpdate
        )
        {
            GUILayout.BeginVertical();

            if (!DrawGroupControl(groupState, ref needUpdate))
            {
                GUILayout.EndVertical();
                return;
            }

            int col = 0;

            bool groupNeedUpdate = false;
            foreach (var tagState in groupState.cacheTagStates)
            {
                if (col == 0)
                {
                    GUILayout.BeginHorizontal();
                }

                DrawTag(filter, groupState, tagState, ref groupNeedUpdate, ref needUpdate);

                if (col == cols - 1)
                {
                    GUILayout.EndHorizontal();
                }
                col = (col + 1) % cols;
            }

            if (col > 0)
            {
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);

            GUILayout.EndVertical();

            if (groupNeedUpdate)
            {
                groupState.ComputeIsInactive();
                needUpdate = true;
            }
        }

        private void DrawErrors()
        {
            if (core.errors.Count == 0 || !core.isShowError.Value)
                return;

            if (GUILayout.Button("Clear Errors"))
            {
                core.errors.Clear();
            }

            foreach (var error in core.errors)
            {
                GUILayout.TextArea(error);
            }
        }

        private void MainWindowFunc(int id)
        {
            var currTarget = core.currTarget;
            if (currTarget == null)
                return;
            if (!core.filterMap.TryGetValue(currTarget, out FilterWrapper filterWrapper))
            {
                return;
            }
            var filter = filterWrapper.filter;

            bool needUpdate = false;

            GUILayout.BeginVertical();
            {
                DrawListSelector();

                scrollPos = GUILayout.BeginScrollView(
                    scrollPos,
                    false,
                    true,
                    GUILayout.ExpandHeight(true)
                );

                DrawErrors();

                GUILayout.BeginVertical();
                foreach (var groupState in filter.cacheGroupStates)
                {
                    // ad-hoc improvement for uploader filter
                    if (groupState.cacheTagStates.Count <= 1 && groupState.group == sourceGroupName)
                        continue;

                    DrawGroup(3, filter, groupState, ref needUpdate);
                }
                GUILayout.EndVertical();

                GUILayout.EndScrollView();

                GUILayout.Space(4);

                DrawGlobalControl(filter, ref needUpdate);
            }
            GUILayout.EndVertical();

            GUI.DragWindow();

            if (needUpdate)
            {
                try
                {
                    core.OnUpdate(currTarget, filter);
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                    core.ShowError(e);
                }
            }
        }

        private static float Scale
        {
            get => Math.Max(Screen.width / 1920.0f, Screen.height / 1080.0f);
        }

        private void UpdateGUI()
        {
            if (core.guiHintPosition is not Vector2 hint)
                return;

            windowRect.x = hint.x;
            windowRect.y = hint.y;

            core.guiHintPosition = null;
        }

        private void OnGUI()
        {
            if (core == null || core.currTarget == null)
                return;

            UpdateGUI();

            var prevSkin = GUI.skin;
            EnsureCustomStyle();
            GUI.skin = customSkin;

            // HiDPI scaling, see <https://github.com/y0soro/Unity.IMGUI.HiDPI.Patcher#implement-hidpi-scaling-for-your-imgui-application>
            var prevMatrix = GUI.matrix;

            var scale = Scale;
            GUI.matrix *= Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));

            windowRect = GUILayout.Window(
                windowID,
                windowRect,
                (GUI.WindowFunction)MainWindowFunc,
                "Chara Filter",
                GUILayout.MinWidth(windowSize.width),
                GUILayout.MinHeight(windowSize.height)
            );

            var enterWindow = windowRect.Contains(Event.current.mousePosition);
            if (enterWindow)
            {
                Input.ResetInputAxes();
            }

            GUI.matrix = prevMatrix;
            GUI.skin = prevSkin;
        }
    }
}
