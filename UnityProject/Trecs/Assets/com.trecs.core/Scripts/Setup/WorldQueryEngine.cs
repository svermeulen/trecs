using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Cached query engine that resolves groups matching tag, component, and exclusion criteria.
    /// Results are memoized so repeated queries within a frame are allocation-free.
    /// </summary>
    internal sealed class WorldQueryEngine
    {
        readonly ReadOnlyFastList<GroupIndex> _allGroups;

        // Indexed by GroupIndex.Index; matches WorldInfo's _groupInfos layout.
        readonly WorldInfo.GroupInfo[] _groupInfos;
        readonly WorldInfo _worldInfo;

        readonly Dictionary<TagSet, TagSetInfo> _tagSetInfos = new();
        readonly Dictionary<int, ReadOnlyFastList<GroupIndex>> _groupsWithComponentsMap = new();
        readonly List<Type> _tempComponentTypeList = new();

        internal WorldQueryEngine(
            ReadOnlyFastList<GroupIndex> allGroups,
            WorldInfo.GroupInfo[] groupInfos,
            WorldInfo worldInfo
        )
        {
            _allGroups = allGroups;
            _groupInfos = groupInfos;
            _worldInfo = worldInfo;
        }

        TagSetInfo GetTagsetInfo(TagSet tagset)
        {
            if (!_tagSetInfos.TryGetValue(tagset, out var info))
            {
                var groupsList = new FastList<GroupIndex>();

                foreach (var group in _allGroups)
                {
                    var hasAllTags = true;

                    var groupTagSet = _worldInfo.ToTagSet(group);
                    var tags = tagset.Tags;
                    foreach (var tag in tags)
                    {
                        if (!groupTagSet.Tags.Contains(tag))
                        {
                            hasAllTags = false;
                            break;
                        }
                    }

                    if (hasAllTags)
                    {
                        groupsList.Add(group);
                    }
                }

                info = new TagSetInfo(tagset, groupsList);
                _tagSetInfos.Add(tagset, info);
            }

            return info;
        }

        int GetComponentsListHash(List<Type> componentTypes)
        {
            TrecsAssert.That(componentTypes.Count > 0);

            var componentsHash = TypeIdProvider.GetTypeId(componentTypes[0]);

            for (int i = 1; i < componentTypes.Count; i++)
            {
                componentsHash ^= TypeIdProvider.GetTypeId(componentTypes[i]);
            }

            return componentsHash;
        }

        public ReadOnlyFastList<GroupIndex> CommonGetTaggedGroupsWithComponents(
            TagSet tagset,
            List<Type> componentTypes
        )
        {
            var componentsHash = GetComponentsListHash(componentTypes);

            var tagsetInfo = GetTagsetInfo(tagset);

            if (!tagsetInfo.WithComponentsSubsets.TryGetValue(componentsHash, out var groups))
            {
                var groupsList = new FastList<GroupIndex>();

                foreach (var group in tagsetInfo.Groups)
                {
                    var hasAllComponents = true;

                    var resolvedTemplate = _worldInfo.GetResolvedTemplateForGroup(group);
                    foreach (var componentType in componentTypes)
                    {
                        if (!resolvedTemplate.HasComponent(componentType))
                        {
                            hasAllComponents = false;
                            break;
                        }
                    }

                    if (hasAllComponents)
                    {
                        groupsList.Add(group);
                    }
                }

                groups = groupsList;
                tagsetInfo.WithComponentsSubsets.Add(componentsHash, groups);
            }

            return groups;
        }

        ReadOnlyFastList<GroupIndex> CommonGetGroupsWithComponents(List<Type> componentTypes)
        {
            var componentsHash = GetComponentsListHash(componentTypes);

            if (!_groupsWithComponentsMap.TryGetValue(componentsHash, out var groups))
            {
                var groupsList = new FastList<GroupIndex>();

                for (int gi = 0; gi < _groupInfos.Length; gi++)
                {
                    var groupInfo = _groupInfos[gi];
                    var hasAllComponents = true;

                    foreach (var componentType in componentTypes)
                    {
                        if (!groupInfo.ResolvedTemplate.HasComponent(componentType))
                        {
                            hasAllComponents = false;
                            break;
                        }
                    }

                    if (hasAllComponents)
                    {
                        groupsList.Add(GroupIndex.FromIndex(gi));
                    }
                }

                groups = groupsList;
                _groupsWithComponentsMap.Add(componentsHash, groups);
            }

            return groups;
        }

        public Template GetSingleTemplateForTags(TagSet tags)
        {
            Template uniqueTemplate = null;

            foreach (var group in GetGroupsWithTags(tags))
            {
                var resolvedTemplate = _worldInfo.GetResolvedTemplateForGroup(group);

                if (uniqueTemplate == null)
                {
                    uniqueTemplate = resolvedTemplate.Template;
                }
                else
                {
                    TrecsAssert.That(
                        uniqueTemplate == resolvedTemplate.Template,
                        "Ambiguous templates found for tags {0} but expected exactly one",
                        tags
                    );
                }
            }

            TrecsAssert.IsNotNull(
                uniqueTemplate,
                "No templates found for tags {0}.  Expected exactly one",
                tags
            );
            return uniqueTemplate;
        }

        public ResolvedTemplate GetResolvedTemplateForTags(TagSet tags)
        {
            // Note that we can't just do this because some templates have multiple groups
            // var group = GetSingleGroupWithTags(tags);
            // return GetResolvedTemplateForGroup(group);

            ResolvedTemplate result = null;

            foreach (var group in GetGroupsWithTags(tags))
            {
                var resolvedTemplate = _worldInfo.GetResolvedTemplateForGroup(group);

                if (result == null)
                {
                    result = resolvedTemplate;
                }
                else
                {
                    TrecsAssert.That(
                        result == resolvedTemplate,
                        "Ambiguous templates found for tags {0} but expected exactly one",
                        tags
                    );
                }
            }

            TrecsAssert.IsNotNull(
                result,
                "No concrete template found for tags {0}.  Expected exactly one",
                tags
            );
            return result;
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags(TagSet tagset)
        {
            var tagsetInfo = GetTagsetInfo(tagset);
            return tagsetInfo.Groups;
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1>()
            where T1 : struct, ITag => GetGroupsWithTags(TagSet<T1>.Value);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => GetGroupsWithTags(TagSet<T1, T2>.Value);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => GetGroupsWithTags(TagSet<T1, T2, T3>.Value);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => GetGroupsWithTags(TagSet<T1, T2, T3, T4>.Value);

        public GroupIndex GetSingleGroupWithTags(TagSet tagset)
        {
            var tagsetInfo = GetTagsetInfo(tagset);

            if (!tagsetInfo.SingleGroup.IsNull)
            {
                return tagsetInfo.SingleGroup;
            }

            var groups = tagsetInfo.Groups;
            TrecsAssert.That(groups.Count > 0, "No groups found for tags {0}", tagset);

            if (groups.Count == 1)
            {
                tagsetInfo.SingleGroup = groups[0];
                return groups[0];
            }

            // Multiple groups contain this tag set as a subset. The tiebreaker
            // only fires when every match belongs to the same registered
            // template — we never resolve silently across template
            // boundaries. Within one template, return the unique match whose
            // tag set is a subset of every other match's (the "narrowest"
            // partition that still contains the query). This lets users elide
            // presence/absence partition tags and inherited base tags when
            // their template has a unique smallest match for the query.
            var firstTemplate = _groupInfos[groups[0].Index].ResolvedTemplate.Template;
            for (int i = 1; i < groups.Count; i++)
            {
                if (_groupInfos[groups[i].Index].ResolvedTemplate.Template != firstTemplate)
                {
                    throw TrecsAssert.CreateException(
                        "Ambiguous groups found for tags {0}.  Matches span multiple templates: {1}.  "
                            + "The resolver only picks a smallest match within one template.  "
                            + "Add a discriminator tag to make the desired template's tag set unique.",
                        tagset,
                        FormatMatches(groups)
                    );
                }
            }

            // Find the match with the fewest tags. If multiple matches tie at
            // that size, throw — within one template's partition lattice,
            // equal-sized matches are siblings (mutex-variant or independent
            // presence/absence dims that differ), so there's no unique
            // minimum.
            int smallestIdx = 0;
            int smallestCount = _groupInfos[groups[0].Index].Tags.Count;
            bool smallestUnique = true;
            for (int i = 1; i < groups.Count; i++)
            {
                var count = _groupInfos[groups[i].Index].Tags.Count;
                if (count < smallestCount)
                {
                    smallestIdx = i;
                    smallestCount = count;
                    smallestUnique = true;
                }
                else if (count == smallestCount)
                {
                    smallestUnique = false;
                }
            }

            if (!smallestUnique)
            {
                throw TrecsAssert.CreateException(
                    "Ambiguous groups found for tags {0}.  Multiple matching groups share the "
                        + "smallest tag-set size: {1}.  Narrow the query with an additional tag.",
                    tagset,
                    FormatMatches(groups)
                );
            }

            // Verify the smallest is actually a subset of every other match.
            // For cross-product partitions (source-gen IPartitionedBy), the
            // unique-smallest check above already implies this. The check is
            // here for templates whose partition shapes are declared manually
            // via the Template constructor's `partitions: TagSet[]` parameter
            // — those can produce matches of differing sizes that don't form
            // a chain in the subset order.
            var smallestGroup = groups[smallestIdx];
            var smallestTags = _groupInfos[smallestGroup.Index].Tags;
            for (int i = 0; i < groups.Count; i++)
            {
                if (i == smallestIdx)
                    continue;

                var otherTags = _groupInfos[groups[i].Index].Tags;
                foreach (var tag in smallestTags)
                {
                    if (!otherTags.Contains(tag))
                    {
                        throw TrecsAssert.CreateException(
                            "Ambiguous groups found for tags {0}.  The smallest match isn't a "
                                + "subset of every other match — matches form siblings rather "
                                + "than a chain: {1}.  Narrow the query with an additional tag.",
                            tagset,
                            FormatMatches(groups)
                        );
                    }
                }
            }

            tagsetInfo.SingleGroup = smallestGroup;
            return smallestGroup;
        }

        // Formats the matching groups as "TemplateName[tag1, tag2]; ..." for
        // inclusion in ambiguous-resolution error messages. Only called on the
        // throw path, so allocation cost doesn't matter.
        string FormatMatches(ReadOnlyFastList<GroupIndex> groups)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < groups.Count; i++)
            {
                if (i > 0)
                    sb.Append("; ");

                var info = _groupInfos[groups[i].Index];
                sb.Append(info.ResolvedTemplate.Template.DebugName);
                sb.Append('[');
                sb.Append(info.TagSet);
                sb.Append(']');
            }
            return sb.ToString();
        }

        public GroupIndex GetSingleGroupWithTags<T1>()
            where T1 : struct, ITag => GetSingleGroupWithTags(TagSet<T1>.Value);

        public GroupIndex GetSingleGroupWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => GetSingleGroupWithTags(TagSet<T1, T2>.Value);

        public GroupIndex GetSingleGroupWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => GetSingleGroupWithTags(TagSet<T1, T2, T3>.Value);

        public GroupIndex GetSingleGroupWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => GetSingleGroupWithTags(TagSet<T1, T2, T3, T4>.Value);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1>(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2>(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));
            _tempComponentTypeList.Add(typeof(T6));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7
        >(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));
            _tempComponentTypeList.Add(typeof(T6));
            _tempComponentTypeList.Add(typeof(T7));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8
        >(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));
            _tempComponentTypeList.Add(typeof(T6));
            _tempComponentTypeList.Add(typeof(T7));
            _tempComponentTypeList.Add(typeof(T8));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9
        >(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));
            _tempComponentTypeList.Add(typeof(T6));
            _tempComponentTypeList.Add(typeof(T7));
            _tempComponentTypeList.Add(typeof(T8));
            _tempComponentTypeList.Add(typeof(T9));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9,
            T10
        >(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
            where T10 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));
            _tempComponentTypeList.Add(typeof(T6));
            _tempComponentTypeList.Add(typeof(T7));
            _tempComponentTypeList.Add(typeof(T8));
            _tempComponentTypeList.Add(typeof(T9));
            _tempComponentTypeList.Add(typeof(T10));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9,
            T10,
            T11
        >(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
            where T10 : unmanaged, IEntityComponent
            where T11 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));
            _tempComponentTypeList.Add(typeof(T6));
            _tempComponentTypeList.Add(typeof(T7));
            _tempComponentTypeList.Add(typeof(T8));
            _tempComponentTypeList.Add(typeof(T9));
            _tempComponentTypeList.Add(typeof(T10));
            _tempComponentTypeList.Add(typeof(T11));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9,
            T10,
            T11,
            T12
        >(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
            where T10 : unmanaged, IEntityComponent
            where T11 : unmanaged, IEntityComponent
            where T12 : unmanaged, IEntityComponent
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(typeof(T1));
            _tempComponentTypeList.Add(typeof(T2));
            _tempComponentTypeList.Add(typeof(T3));
            _tempComponentTypeList.Add(typeof(T4));
            _tempComponentTypeList.Add(typeof(T5));
            _tempComponentTypeList.Add(typeof(T6));
            _tempComponentTypeList.Add(typeof(T7));
            _tempComponentTypeList.Add(typeof(T8));
            _tempComponentTypeList.Add(typeof(T9));
            _tempComponentTypeList.Add(typeof(T10));
            _tempComponentTypeList.Add(typeof(T11));
            _tempComponentTypeList.Add(typeof(T12));

            return CommonGetTaggedGroupsWithComponents(tagset, _tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(Type componentType)
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);
            _tempComponentTypeList.Add(componentType6);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);
            _tempComponentTypeList.Add(componentType6);
            _tempComponentTypeList.Add(componentType7);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7,
            Type componentType8
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);
            _tempComponentTypeList.Add(componentType6);
            _tempComponentTypeList.Add(componentType7);
            _tempComponentTypeList.Add(componentType8);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7,
            Type componentType8,
            Type componentType9
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);
            _tempComponentTypeList.Add(componentType6);
            _tempComponentTypeList.Add(componentType7);
            _tempComponentTypeList.Add(componentType8);
            _tempComponentTypeList.Add(componentType9);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7,
            Type componentType8,
            Type componentType9,
            Type componentType10
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);
            _tempComponentTypeList.Add(componentType6);
            _tempComponentTypeList.Add(componentType7);
            _tempComponentTypeList.Add(componentType8);
            _tempComponentTypeList.Add(componentType9);
            _tempComponentTypeList.Add(componentType10);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7,
            Type componentType8,
            Type componentType9,
            Type componentType10,
            Type componentType11
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);
            _tempComponentTypeList.Add(componentType6);
            _tempComponentTypeList.Add(componentType7);
            _tempComponentTypeList.Add(componentType8);
            _tempComponentTypeList.Add(componentType9);
            _tempComponentTypeList.Add(componentType10);
            _tempComponentTypeList.Add(componentType11);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7,
            Type componentType8,
            Type componentType9,
            Type componentType10,
            Type componentType11,
            Type componentType12
        )
        {
            // Use a cached list to avoid any per frame allocs
            _tempComponentTypeList.Clear();
            _tempComponentTypeList.Add(componentType1);
            _tempComponentTypeList.Add(componentType2);
            _tempComponentTypeList.Add(componentType3);
            _tempComponentTypeList.Add(componentType4);
            _tempComponentTypeList.Add(componentType5);
            _tempComponentTypeList.Add(componentType6);
            _tempComponentTypeList.Add(componentType7);
            _tempComponentTypeList.Add(componentType8);
            _tempComponentTypeList.Add(componentType9);
            _tempComponentTypeList.Add(componentType10);
            _tempComponentTypeList.Add(componentType11);
            _tempComponentTypeList.Add(componentType12);

            return CommonGetGroupsWithComponents(_tempComponentTypeList);
        }

        #region Generalized GroupIndex Query

        readonly Dictionary<GroupQueryKey, ReadOnlyFastList<GroupIndex>> _groupQueryCache = new();

        internal ReadOnlyFastList<GroupIndex> ResolveGroups(GroupQueryKey key)
        {
            if (_groupQueryCache.TryGetValue(key, out var cached))
                return cached;

            var result = ResolveGroupsUncached(key);
            _groupQueryCache[key] = result;
            return result;
        }

        ReadOnlyFastList<GroupIndex> ResolveGroupsUncached(GroupQueryKey key)
        {
            ReadOnlyFastList<GroupIndex> baseGroups;
            if (!key.PositiveTags.IsNull)
                baseGroups = GetTagsetInfo(key.PositiveTags).Groups;
            else
                baseGroups = _allGroups;

            var resultList = new FastList<GroupIndex>();

            foreach (var group in baseGroups)
            {
                if (key.HasNegativeTags)
                {
                    var groupTagSet = _worldInfo.ToTagSet(group);
                    var negativeTags = key.NegativeTags.Tags;
                    bool hasNegativeTag = false;
                    foreach (var tag in negativeTags)
                    {
                        if (groupTagSet.Tags.Contains(tag))
                        {
                            hasNegativeTag = true;
                            break;
                        }
                    }
                    if (hasNegativeTag)
                        continue;
                }

                if (key.HasPositiveComponents || key.HasNegativeComponents)
                {
                    var resolvedTemplate = _worldInfo.GetResolvedTemplateForGroup(group);

                    if (key.HasPositiveComponents)
                    {
                        bool hasAll = true;
                        foreach (var compId in key.PositiveComponents.Components)
                        {
                            var type = TypeIdProvider.GetTypeFromId(compId.Value);
                            if (!resolvedTemplate.HasComponent(type))
                            {
                                hasAll = false;
                                break;
                            }
                        }
                        if (!hasAll)
                            continue;
                    }

                    if (key.HasNegativeComponents)
                    {
                        bool hasNegative = false;
                        foreach (var compId in key.NegativeComponents.Components)
                        {
                            var type = TypeIdProvider.GetTypeFromId(compId.Value);
                            if (resolvedTemplate.HasComponent(type))
                            {
                                hasNegative = true;
                                break;
                            }
                        }
                        if (hasNegative)
                            continue;
                    }
                }

                resultList.Add(group);
            }

            return resultList;
        }

        #endregion

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1>()
            where T1 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(typeof(T1));
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(typeof(T1), typeof(T2));
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(typeof(T1), typeof(T2), typeof(T3));
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(typeof(T1), typeof(T2), typeof(T3), typeof(T4));
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5)
            );
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5, T6>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5),
                typeof(T6)
            );
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5),
                typeof(T6),
                typeof(T7)
            );
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8
        >()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5),
                typeof(T6),
                typeof(T7),
                typeof(T8)
            );
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9
        >()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5),
                typeof(T6),
                typeof(T7),
                typeof(T8),
                typeof(T9)
            );
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9,
            T10
        >()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
            where T10 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5),
                typeof(T6),
                typeof(T7),
                typeof(T8),
                typeof(T9),
                typeof(T10)
            );
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9,
            T10,
            T11
        >()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
            where T10 : unmanaged, IEntityComponent
            where T11 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5),
                typeof(T6),
                typeof(T7),
                typeof(T8),
                typeof(T9),
                typeof(T10),
                typeof(T11)
            );
        }

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<
            T1,
            T2,
            T3,
            T4,
            T5,
            T6,
            T7,
            T8,
            T9,
            T10,
            T11,
            T12
        >()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent
            where T9 : unmanaged, IEntityComponent
            where T10 : unmanaged, IEntityComponent
            where T11 : unmanaged, IEntityComponent
            where T12 : unmanaged, IEntityComponent
        {
            return GetGroupsWithComponents(
                typeof(T1),
                typeof(T2),
                typeof(T3),
                typeof(T4),
                typeof(T5),
                typeof(T6),
                typeof(T7),
                typeof(T8),
                typeof(T9),
                typeof(T10),
                typeof(T11),
                typeof(T12)
            );
        }

        class TagSetInfo
        {
            public TagSetInfo(TagSet tagSet, ReadOnlyFastList<GroupIndex> groups)
            {
                TagSet = tagSet;
                Groups = groups;
            }

            public TagSet TagSet { get; }
            public ReadOnlyFastList<GroupIndex> Groups { get; }

            // Lazy cache for GetSingleGroupWithTags. Null until first
            // successful resolve; ambiguous queries don't populate it (errors
            // aren't on a hot path and re-throwing is fine).
            //
            // Relies on `default(GroupIndex) == GroupIndex.Null` so a freshly
            // constructed TagSetInfo reads as "not cached" without an extra
            // initializer. If GroupIndex's null sentinel ever stops being the
            // default value, initialize this field explicitly.
            public GroupIndex SingleGroup;

            public DenseDictionary<
                int,
                ReadOnlyFastList<GroupIndex>
            > WithComponentsSubsets { get; } = new();
        }
    }
}
