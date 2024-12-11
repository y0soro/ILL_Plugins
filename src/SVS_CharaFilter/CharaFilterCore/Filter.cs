using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace CharaFilterCore;

public abstract class FilterContextBase
{
    internal static ManualLogSource Log;

    internal readonly Dictionary<string, GroupFilterState> _groupStates = [];
    internal List<GroupFilterState> cacheGroupStates = [];

    internal ItemInfo activeItemInfo = null;

    internal bool uiIsMutexMode = true;

    internal void ResetAll()
    {
        foreach (var groupState in cacheGroupStates)
        {
            groupState.Reset(true);
        }
    }

    internal void PopulateActiveItemState()
    {
        if (activeItemInfo == null)
            return;

        uiIsMutexMode = false;

        foreach (var groupState in cacheGroupStates)
        {
            if (
                !activeItemInfo.groups.TryGetValue(
                    groupState.group,
                    out ItemInfo.GroupTags groupTags
                )
            )
            {
                groupState.Reset();
                continue;
            }

            groupState.isInactive = true;
            foreach (var tagState in groupState.cacheTagStates)
            {
                var hasTag = groupTags.tags.ContainsKey(tagState.tag);
                tagState.op = hasTag ? TagMatchOp.FilterIn : TagMatchOp.Inactive;
                groupState.isInactive = false;
            }

            if (!groupState.isInactive)
            {
                groupState.isFilterByAnd = true;
                groupState.uiCollapsed = false;
            }
        }
    }

    public void CollectNew(IEnumerable<ItemInfo> list)
    {
        activeItemInfo = null;

        HashSet<string> toDeleteGroup = [.. _groupStates.Keys];
        Dictionary<string, HashSet<string>> toDeleteGroupTags = [];

        foreach (var group in _groupStates.Values)
        {
            toDeleteGroupTags[group.group] = [.. group._tagStates.Keys];
        }

#if false
        {
            var info = new ItemInfo();
            info1.AddGroup("Empty", -400, true);
            info1.AddGroup("Dummy", -300, true);
            info1.AddTag("dummy", "Dummy");

            list = list.Append(info);
        }
#endif

        foreach (var info in list)
        {
            foreach (var group in info.groups.Values)
            {
                HashSet<string> toDeleteTags;
                if (_groupStates.TryGetValue(group.group, out GroupFilterState groupState))
                {
                    if (!toDeleteGroupTags.TryGetValue(group.group, out toDeleteTags))
                    {
                        toDeleteTags = null;
                    }
                    toDeleteGroup.Remove(group.group);

                    if (group.order is int order)
                    {
                        groupState.order = order;
                    }

                    if (group.isMutex is bool isMutex)
                    {
                        groupState.isMutex = isMutex;
                    }
                }
                else
                {
                    _groupStates.Add(
                        group.group,
                        new GroupFilterState
                        {
                            group = group.group,
                            order = group.order.GetValueOrDefault(),
                            isMutex = group.isMutex.GetValueOrDefault(),
                            isFilterByAnd = false,
                            isInactive = true,
                            _tagStates = [],
                            cacheTagStates = [],
                        }
                    );
                    toDeleteTags = null;
                    groupState = _groupStates[group.group];
                }

                foreach (var tag in group.tags.Values)
                {
                    toDeleteTags?.Remove(tag.tag);

                    if (groupState._tagStates.TryGetValue(tag.tag, out TagState tagState))
                    {
                        if (tag.order is int order)
                        {
                            tagState.order = order;
                        }
                    }
                    else
                    {
                        groupState._tagStates.Add(
                            tag.tag,
                            new TagState
                            {
                                tag = tag.tag,
                                order = tag.order.GetValueOrDefault(),
                                op = TagMatchOp.Inactive,
                            }
                        );
                    }
                }
            }
        }

        foreach (var group in toDeleteGroup)
        {
            _groupStates.Remove(group);
            toDeleteGroupTags.Remove(group);
        }

        foreach (var item in toDeleteGroupTags)
        {
            var groupState = _groupStates[item.Key];
            foreach (var tag in item.Value)
            {
                groupState._tagStates.Remove(tag);
            }
        }

        foreach (var group in _groupStates.Values)
        {
            group.cacheTagStates =
            [
                .. group
                    ._tagStates.Values.OrderBy(
                        (TagState v) =>
                        {
                            return v.order;
                        }
                    )
                    .ThenBy(
                        (TagState v) =>
                        {
                            return v.tag;
                        }
                    ),
            ];
            group.ComputeIsInactive();
        }

        cacheGroupStates =
        [
            .. _groupStates
                .Values.OrderBy(
                    (GroupFilterState v) =>
                    {
                        return v.order;
                    }
                )
                .ThenBy(
                    (GroupFilterState v) =>
                    {
                        return v.group;
                    }
                ),
        ];
    }

    public bool FilterIn(ItemInfo info)
    {
        // AND with each group
        foreach (var groupState in cacheGroupStates)
        {
            bool filterInGroup()
            {
                if (groupState.isInactive)
                    return true;
                if (!info.groups.TryGetValue(groupState.group, out ItemInfo.GroupTags itemTags))
                {
                    // return groupState.cacheTagStates.Count == 0 || !groupState.isFilterByAnd;
                    return groupState.cacheTagStates.Count == 0;
                }

                bool isActive = false;

                foreach (var tagState in groupState.cacheTagStates)
                {
                    if (tagState.op == TagMatchOp.Inactive)
                        continue;

                    isActive = true;

                    if (itemTags.tags.ContainsKey(tagState.tag))
                    {
                        if (tagState.op == TagMatchOp.FilterIn && !groupState.isFilterByAnd)
                            return true;
                        if (tagState.op == TagMatchOp.FilterOut)
                            return false;
                    }
                    else if (tagState.op == TagMatchOp.FilterIn && groupState.isFilterByAnd)
                    {
                        return false;
                    }
                }

                return !isActive || groupState.isFilterByAnd;
            }

            if (!filterInGroup())
                return false;
        }

        return true;
    }

    internal enum TagMatchOp
    {
        Inactive,
        FilterIn,
        FilterOut,
    }

    internal class TagState
    {
        internal string tag;
        internal int order;

        internal TagMatchOp op;
    }

    internal class GroupFilterState
    {
        internal string group;
        internal int order;
        internal bool isMutex;

        internal bool isFilterByAnd;

        internal bool isInactive;

        internal bool uiCollapsed = false;

        internal Dictionary<string, TagState> _tagStates = [];
        internal List<TagState> cacheTagStates = [];

        internal bool AllowMultiSelect
        {
            get => !(isFilterByAnd && isMutex);
        }

        internal void Reset(bool resetFilterMod = false, TagMatchOp op = TagMatchOp.Inactive)
        {
            foreach (var tagState in cacheTagStates)
            {
                tagState.op = op;
            }
            if (resetFilterMod)
                isFilterByAnd = false;
            isInactive = op == TagMatchOp.Inactive;
        }

        internal void ComputeIsInactive()
        {
            foreach (var tagState in cacheTagStates)
            {
                if (tagState.op != TagMatchOp.Inactive)
                {
                    isInactive = false;
                    return;
                }
            }

            isInactive = true;
        }
    }

    public class ItemInfo
    {
        static readonly string defaultGroup = "Default Group";

        internal readonly Dictionary<string, GroupTags> groups = [];

        internal class GroupTags
        {
            internal string group;

            internal int? order;

            internal bool? isMutex;

            internal Dictionary<string, Tag> tags;

            public GroupTags() { }
        }

        internal class Tag
        {
            internal string tag;

            internal int? order;
        }

        public static ItemInfo FromIllInfo<T>(
            string FullPath,
            string Personality,
            bool IsDefaultData,
            bool IsMyData
        )
            where T : class
        {
            var info = new ItemInfo();

            var gSource = L10n.Group("Source");
            info.AddGroup(gSource, -200, true);
            if (IsDefaultData)
                info.AddTag(L10n.Tag("Presets"), gSource, 2);
            else if (IsMyData)
                info.AddTag(L10n.Tag("My Work"), gSource, 0);
            else
                info.AddTag(L10n.Tag("Others Work"), gSource, 1);

            if (!Personality.IsNullOrEmpty())
            {
                var gPersonality = L10n.Group("Personality");
                info.AddGroup(gPersonality, -100, true);
                info.AddTag(L10n.Tag(Personality), gPersonality);
            }

            info.AddIllPngPath(FullPath);

            return info;
        }

        public void AddGroup(string group, int? order, bool? isMutex)
        {
            groups[group] = new GroupTags
            {
                group = group,
                order = order,
                isMutex = isMutex,
                tags = [],
            };
        }

        public void AddDefaultGroup()
        {
            AddGroup(L10n.Group(defaultGroup), 100, false);
        }

        public void AddTag(string tag, string group = null, int? order = null)
        {
            if (group == null)
            {
                AddDefaultGroup();
                group = L10n.Group(defaultGroup);
            }
            var groupTags = groups[group];
            groupTags.tags[tag] = new Tag { tag = tag, order = order };
        }

        private bool AddPathTag(string pathTag)
        {
            string tag = "";
            string group = null;
            int? groupOrder = null;
            int? tagOrder = null;
            bool isMutex = true;

            static bool CmpArg(string a, string b)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }

            void ParseModifier(string modifier)
            {
                var parts = modifier.Split("=");
                if (parts == null || parts.Length != 2)
                    return;
                var arg = parts[0];
                var value = parts[1];

                if (CmpArg(arg, "g") || CmpArg(arg, "group"))
                {
                    group = value;
                }
                else if (CmpArg(arg, "go") || CmpArg(arg, "groupOrder"))
                {
                    if (int.TryParse(value, out int order))
                    {
                        groupOrder = order;
                    }
                }
                else if (
                    CmpArg(arg, "o")
                    || CmpArg(arg, "order")
                    || CmpArg(arg, "to")
                    || CmpArg(arg, "tagOrder")
                )
                {
                    if (int.TryParse(value, out int order))
                    {
                        tagOrder = order;
                    }
                }
                else if (CmpArg(arg, "m"))
                {
                    if (int.TryParse(value, out int multiSelect))
                    {
                        isMutex = multiSelect == 0;
                    }
                }
                else
                {
                    Log.LogInfo($"Unknown tag modifier {modifier}");
                }
            }

            int prevEnd = 0;
            int start = -1;
            for (var i = 0; i < pathTag.Length; i++)
            {
                if (start < 0)
                {
                    if (pathTag[i] == '{')
                    {
                        start = i;
                        if (i > prevEnd)
                        {
                            tag += pathTag[prevEnd..i];
                        }
                    }
                }
                else if (pathTag[i] == '}')
                {
                    ParseModifier(pathTag[(start + 1)..i]);
                    prevEnd = i + 1;
                    start = -1;
                }
            }

            if (pathTag.Length > prevEnd)
            {
                tag += pathTag[prevEnd..];
            }

            if (tag.Length == 0)
                return false;

            if (!group.IsNullOrEmpty())
            {
                AddGroup(group, groupOrder, isMutex);
            }

            AddTag(tag, group, tagOrder);
            return true;
        }

        public void AddIllPngPath(string path)
        {
            AddDefaultGroup();
            path = Path.GetRelativePath(Application.dataPath, path);

            bool hasTag = false;
            // trim *\female\ or *\male\
            var token = "male\\";
            var idx = path.IndexOf(token);
            if (idx >= 0)
            {
                path = path[(idx + token.Length)..];
            }

            char[] sep = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
            var paths = path.Split(sep, StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < paths.Length - 1; i++)
            {
                if (AddPathTag(paths[i]))
                {
                    hasTag = true;
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(paths[^1]);

            int start = -1;
            for (var i = 0; i < fileName.Length; i++)
            {
                if (start < 0)
                {
                    if (fileName[i] == '[')
                    {
                        start = i;
                    }
                }
                else if (fileName[i] == ']')
                {
                    if (AddPathTag(fileName[(start + 1)..i]))
                    {
                        hasTag = true;
                    }
                    start = -1;
                }
            }

            if (!hasTag)
            {
                AddDefaultGroup();
                AddTag(L10n.Tag("Untagged"), order: -1);
            }
        }
    }
}

public abstract class FilterContext<T> : FilterContextBase
    where T : class
{
    private readonly Dictionary<T, ItemInfo> infoCache = [];
    private readonly object lockObj = new();

    protected virtual ItemInfo ConvertItemInfo(T item)
    {
        return new ItemInfo();
    }

    private ItemInfo ConvertAddItemInfo(T item)
    {
        var info = ConvertItemInfo(item);
        if (info == null)
            return null;
        infoCache.TryAdd(item, info);
        return info;
    }

    private ItemInfo GetItemInfo(T item)
    {
        if (!infoCache.TryGetValue(item, out ItemInfo info))
        {
            Log.LogDebug("FIXME: no cached item info");
            info = ConvertAddItemInfo(item);
        }
        return info;
    }

    public void SetActiveItem(T item)
    {
        lock (lockObj)
        {
            activeItemInfo = item == null ? null : GetItemInfo(item);
        }
    }

    public void CollectNew(IEnumerable<T> list)
    {
        lock (lockObj)
        {
            infoCache.Clear();
            CollectNew(list.Select(ConvertAddItemInfo).Where((info) => info != null));
        }
    }

    public bool FilterIn(T item)
    {
        lock (lockObj)
        {
            var info = GetItemInfo(item);
            if (info == null)
            {
                // XXX: or keep the original visibility?
                return false;
            }
            return FilterIn(info);
        }
    }
}
