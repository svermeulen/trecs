using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only metadata about all registered templates, groups, components, and tags in a world.
    /// Use to discover which groups exist, resolve tag sets to groups, and inspect component layouts.
    /// Accessed via <see cref="WorldAccessor.WorldInfo"/> or <see cref="World.WorldInfo"/>.
    /// </summary>
    public class WorldInfo
    {
        static readonly TrecsLog _log = new(nameof(WorldInfo));

        readonly ReadOnlyDenseDictionary<GroupIndex, GroupInfo> _groupInfos;
        readonly Dictionary<Template, FastList<GroupIndex>> _templateGroupsMap;
        readonly ReadOnlyFastList<GroupIndex> _allGroups;
        readonly Dictionary<TagSet, GroupIndex> _tagSetToIndex;
        readonly TagSet[] _indexToTagSet;
        readonly ReadOnlyFastList<ResolvedTemplate> _resolvedTemplates;
        readonly HashSet<Template> _resolvedTemplateSet = new();
        readonly HashSet<Template> _allTemplatesSet = new();
        readonly ReadOnlyFastList<Template> _allTemplates;
        readonly ResolvedTemplate _globalTemplate;
        readonly WorldQueryEngine _queryEngine;

        const int _globalEntitySlotIndex = 0;

        readonly GroupIndex _globalGroup;
        readonly EntityIndex _globalEntityIndex;
        readonly ReadOnlyFastList<GroupIndex> _globalGroups;

        public WorldInfo(IReadOnlyList<Template> templatesList)
        {
            if (!HasGlobalsTemplate(templatesList))
            {
                templatesList = new List<Template>(templatesList)
                {
                    TrecsTemplates.Globals.Template,
                };
            }

            var resolvedTemplatesList = new FastList<ResolvedTemplate>(templatesList.Count);

            foreach (var template in templatesList)
            {
                resolvedTemplatesList.Add(ResolveTemplate(template));
            }

            // Pass 1: assign a sequential GroupIndex to each unique TagSet that
            // appears as a group on any resolved template.
            var tagSetToIndex = new Dictionary<TagSet, GroupIndex>();
            var indexToTagSet = new List<TagSet>();

            foreach (var resolvedTemplate in resolvedTemplatesList)
            {
                foreach (var tagSet in resolvedTemplate.GroupTagSets)
                {
                    if (!tagSetToIndex.ContainsKey(tagSet))
                    {
                        Assert.That(
                            indexToTagSet.Count <= ushort.MaxValue,
                            "GroupIndex is ushort — world would have more than {} groups",
                            ushort.MaxValue
                        );
                        var idx = new GroupIndex((ushort)indexToTagSet.Count);
                        tagSetToIndex.Add(tagSet, idx);
                        indexToTagSet.Add(tagSet);
                    }
                }
            }

            // Populate ResolvedTemplate.Groups now that we have the mapping.
            foreach (var resolvedTemplate in resolvedTemplatesList)
            {
                var groups = new List<GroupIndex>(resolvedTemplate.GroupTagSets.Count);
                foreach (var tagSet in resolvedTemplate.GroupTagSets)
                {
                    groups.Add(tagSetToIndex[tagSet]);
                }
                resolvedTemplate.Groups = groups;
            }

            var groupTemplateMap = new DenseDictionary<GroupIndex, GroupInfo>();
            var templateGroupsMap = new Dictionary<Template, FastList<GroupIndex>>();
            var allGroups = new FastList<GroupIndex>();
            var allTemplates = new FastList<Template>();

            void AddGroupToTemplate(Template template, GroupIndex group)
            {
                if (templateGroupsMap.TryGetValue(template, out var existingGroups))
                {
                    existingGroups.Add(group);
                }
                else
                {
                    templateGroupsMap.Add(template, new FastList<GroupIndex> { group });
                }
            }

            ResolvedTemplate globalTemplate = null;
            GroupIndex? globalGroup = null;

            foreach (var resolvedTemplate in resolvedTemplatesList)
            {
                Assert.That(
                    resolvedTemplate.AllTags.Tags.Count > 0,
                    "Template {} must have at least one tag",
                    resolvedTemplate.DebugName
                );

                if (
                    resolvedTemplate.Template == TrecsTemplates.Globals.Template
                    || resolvedTemplate.AllBaseTemplates.Contains(TrecsTemplates.Globals.Template)
                )
                {
                    Assert.That(
                        globalTemplate == null,
                        "Found multiple global entity types.  There can only be one."
                    );
                    globalTemplate = resolvedTemplate;

                    Assert.That(
                        resolvedTemplate.Groups.Count == 1,
                        "Global entity type must only be in one group"
                    );
                    Assert.That(!globalGroup.HasValue);
                    globalGroup = resolvedTemplate.Groups[0];
                }

                var templateGroupsSvList = new FastList<GroupIndex>();

                for (int i = 0; i < resolvedTemplate.Groups.Count; i++)
                {
                    var group = resolvedTemplate.Groups[i];
                    var groupTagSet = resolvedTemplate.GroupTagSets[i];

                    Assert.That(
                        !groupTemplateMap.ContainsKey(group),
                        "Found same group {} added multiple times.  Groups must be unique.",
                        group
                    );

                    groupTemplateMap.Add(
                        group,
                        new GroupInfo(group, groupTagSet, resolvedTemplate)
                    );

                    AddGroupToTemplate(resolvedTemplate.Template, group);

                    foreach (var baseTemplate in resolvedTemplate.AllBaseTemplates)
                    {
                        AddGroupToTemplate(baseTemplate, group);
                    }

                    allGroups.Add(group);
                    templateGroupsSvList.Add(group);
                }

                var wasAdded = _resolvedTemplateSet.Add(resolvedTemplate.Template);
                Assert.That(wasAdded);
            }

            foreach (var resolvedTemplate in resolvedTemplatesList)
            {
                if (_allTemplatesSet.Add(resolvedTemplate.Template))
                {
                    allTemplates.Add(resolvedTemplate.Template);
                }

                foreach (var baseType in resolvedTemplate.AllBaseTemplates)
                {
                    // If we allow this, then as one example, it's not possible to add an
                    // entity to the base type group based on provided tagset, since we
                    // would match to both groups
                    // We could fix by requiring that the provided Add tagset exactly
                    // matches group but this is limiting since we can't have factories
                    // decoupled from templates
                    // We could automagically choose the group that matches the given
                    // tagsets best but then we won't catch real errors when truly
                    // ambiguous tagsets are used
                    // So game code just needs to create a dedicated base template I guess for now
                    Assert.That(
                        !_resolvedTemplateSet.Contains(baseType),
                        "Provided entity types must not be base types of other provided entity types.  Found {} as a base type of {}",
                        baseType,
                        resolvedTemplate
                    );

                    if (_allTemplatesSet.Add(baseType))
                    {
                        allTemplates.Add(baseType);
                    }
                }
            }

            Assert.IsNotNull(globalTemplate);

            _globalTemplate = globalTemplate;
            _globalGroup = globalGroup.Value;
            _globalGroups = new FastList<GroupIndex>(new[] { globalGroup.Value });
            _globalEntityIndex = new(_globalEntitySlotIndex, _globalGroup);

            _groupInfos = new(groupTemplateMap);
            _templateGroupsMap = templateGroupsMap;
            _allGroups = allGroups;
            _resolvedTemplates = resolvedTemplatesList;
            _allTemplates = allTemplates;

            _indexToTagSet = indexToTagSet.ToArray();
            _tagSetToIndex = tagSetToIndex;

            _queryEngine = new WorldQueryEngine(_allGroups, _groupInfos, this);
        }

        /// <summary>
        /// Resolves a <see cref="TagSet"/> to the sequential <see cref="GroupIndex"/>
        /// assigned at world-build time. The mapping is fixed once the world is built.
        /// </summary>
        public GroupIndex ToGroupIndex(TagSet tagSet)
        {
            if (_tagSetToIndex.TryGetValue(tagSet, out var index))
            {
                return index;
            }

            throw Assert.CreateException("No group registered for tag set {}", tagSet);
        }

        /// <summary>
        /// Returns the <see cref="TagSet"/> associated with a <see cref="GroupIndex"/>.
        /// </summary>
        public TagSet ToTagSet(GroupIndex groupIndex)
        {
            Assert.That(
                groupIndex.Value < _indexToTagSet.Length,
                "GroupIndex {} out of range [0, {})",
                groupIndex.Value,
                _indexToTagSet.Length
            );
            return _indexToTagSet[groupIndex.Value];
        }

        static bool HasGlobalsTemplate(IReadOnlyList<Template> templates)
        {
            HashSet<Template> seen = new();
            foreach (var template in templates)
            {
                seen.Clear();

                if (IsOrInheritsFrom(template, TrecsTemplates.Globals.Template, seen))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsOrInheritsFrom(
            Template template,
            Template target,
            HashSet<Template> visited = null
        )
        {
            if (template == target)
            {
                return true;
            }

            visited ??= new HashSet<Template>();

            if (!visited.Add(template))
            {
                return false;
            }

            foreach (var baseTemplate in template.LocalBaseTemplates)
            {
                if (IsOrInheritsFrom(baseTemplate, target, visited))
                {
                    return true;
                }
            }

            return false;
        }

        static ResolvedTemplate ResolveTemplate(Template template)
        {
            _log.Trace("Building entity with name {}", template.DebugName);

            var enqueuedBaseTypes = new HashSet<Template>();
            var allBaseTypesList = new List<Template>();
            var baseTypeProcessQueue = new Queue<Template>();

            foreach (var baseType in template.LocalBaseTemplates)
            {
                baseTypeProcessQueue.Enqueue(baseType);
                enqueuedBaseTypes.Add(baseType);
            }

            while (!baseTypeProcessQueue.IsEmpty())
            {
                var baseType = baseTypeProcessQueue.Dequeue();
                Assert.That(enqueuedBaseTypes.Contains(baseType));
                allBaseTypesList.Add(baseType);
                _log.Trace(
                    "Added base type {} to entity {}",
                    baseType.DebugName,
                    template.DebugName
                );

                foreach (var baseBaseType in baseType.LocalBaseTemplates)
                {
                    if (enqueuedBaseTypes.Add(baseBaseType))
                    {
                        baseTypeProcessQueue.Enqueue(baseBaseType);
                    }
                }
            }

            var allComponentDecMap = new DenseDictionary<Type, List<IComponentDeclaration>>();

            void ProcessComponentDec(IComponentDeclaration componentDec)
            {
                var componentType = componentDec.ComponentType;

                if (!allComponentDecMap.TryGetValue(componentType, out var decs))
                {
                    decs = new();
                    allComponentDecMap.Add(componentType, decs);
                }

                decs.Add(componentDec);
            }

            foreach (var componentDec in template.LocalComponentDeclarations)
            {
                ProcessComponentDec(componentDec);
            }

            foreach (var baseType in allBaseTypesList)
            {
                foreach (var componentDec in baseType.LocalComponentDeclarations)
                {
                    ProcessComponentDec(componentDec);
                }
            }

            var allComponentDecs = new List<IResolvedComponentDeclaration>();
            var componentBuilders = new List<IComponentBuilder>();
            var allResolvedComponentDecMap =
                new DenseDictionary<Type, IResolvedComponentDeclaration>();

            foreach (var (componentType, componentDecs) in allComponentDecMap)
            {
                Assert.That(componentDecs.Count > 0);

                var resolvedDecs = componentDecs[0]
                    .MergeAsConcrete(componentDecs, template.DebugName);

                foreach (var resolvedDec in resolvedDecs)
                {
                    allComponentDecs.Add(resolvedDec);
                    componentBuilders.Add(resolvedDec.Builder);
                    allResolvedComponentDecMap.Add(resolvedDec.ComponentType, resolvedDec);
                }
            }

            foreach (var baseType in template.LocalBaseTemplates)
            {
                Assert.That(
                    baseType.Partitions.IsEmpty(),
                    "Entity type partitions must only be specified in the concrete entity type"
                );
            }

            var allTags = new List<Tag>();
            var allPartitions = new List<TagSet>();

            allPartitions.AddRange(template.Partitions);
            allTags.AddRange(template.LocalTags);

            foreach (var baseType in allBaseTypesList)
            {
                allTags.AddRange(baseType.LocalTags);
                allPartitions.AddRange(baseType.Partitions);
            }

            var tagset = TagSet.FromTags(allTags);

            return new(
                template: template,
                groupTagSets: CalculateTemplateGroupTagSets(tagset, allPartitions),
                allBaseTemplates: allBaseTypesList,
                partitions: allPartitions,
                componentDeclarations: allComponentDecs,
                componentDeclarationMap: new(allResolvedComponentDecMap),
                componentBuilders: componentBuilders.ToArray(),
                tagset: tagset
            );

            static IReadOnlyList<TagSet> CalculateTemplateGroupTagSets(
                TagSet tags,
                IReadOnlyList<TagSet> partitions
            )
            {
                var groups = new List<TagSet>();

                var groupTags = partitions.Select(x => x.Tags.ToList()).ToList();

                if (groupTags.IsEmpty())
                {
                    groupTags.Add(new List<Tag>());
                }

                foreach (var tagList in groupTags)
                {
                    tagList.AddRange(tags.Tags);
                    groups.Add(TagSet.FromTags(tagList));
                }

                return groups;
            }
        }

        public WorldQueryEngine QueryEngine => _queryEngine;

        public ReadOnlyFastList<ResolvedTemplate> ResolvedTemplates
        {
            get { return _resolvedTemplates; }
        }

        public GroupIndex GlobalGroup
        {
            get { return _globalGroup; }
        }

        public ReadOnlyFastList<GroupIndex> GlobalGroups
        {
            get { return _globalGroups; }
        }

        public ReadOnlyFastList<GroupIndex> AllGroups
        {
            get { return _allGroups; }
        }

        public ReadOnlyFastList<Template> AllTemplates
        {
            get { return _allTemplates; }
        }

        public EntityIndex GlobalEntityIndex
        {
            get { return _globalEntityIndex; }
        }

        public ResolvedTemplate GlobalTemplate
        {
            get { return _globalTemplate; }
        }

        public ReadOnlyFastList<GroupIndex> GetTemplateGroups(Template template)
        {
            if (_templateGroupsMap.TryGetValue(template, out var groups))
            {
                return groups;
            }

            throw Assert.CreateException("No groups found for template {}", template);
        }

        public ReadOnlyDenseHashSet<Tag> GetGroupTags(GroupIndex group)
        {
            if (_groupInfos.TryGetValue(group, out var groupInfo))
            {
                return groupInfo.Tags;
            }

            throw Assert.CreateException("Unrecognized group {}", group);
        }

        public Template GetSingleTemplateForTags(TagSet tags) =>
            _queryEngine.GetSingleTemplateForTags(tags);

        public ResolvedTemplate GetResolvedTemplateForGroup(GroupIndex group)
        {
            if (_groupInfos.TryGetValue(group, out var info))
            {
                return info.ResolvedTemplate;
            }

            throw Assert.CreateException("No entity type found for group {}", group);
        }

        public ResolvedTemplate GetResolvedTemplateForTags(TagSet tags) =>
            _queryEngine.GetResolvedTemplateForTags(tags);

        public bool IsResolvedTemplate(Template template)
        {
            return _resolvedTemplateSet.Contains(template);
        }

        public bool GroupIsTemplate(GroupIndex group, Template template)
        {
            var resolvedTemplate = GetResolvedTemplateForGroup(group);
            return resolvedTemplate.Template == template
                || resolvedTemplate.AllBaseTemplates.Contains(template);
        }

        public ReadOnlyFastList<GroupIndex> CommonGetTaggedGroupsWithComponents(
            TagSet tagset,
            List<Type> componentTypes
        ) => _queryEngine.CommonGetTaggedGroupsWithComponents(tagset, componentTypes);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1>(TagSet tagset)
            where T1 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1>(tagset);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2>(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2>(tagset);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3>(tagset);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4>(tagset);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5>(tagset);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6>(tagset);

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
            where T7 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6, T7>(tagset);

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
            where T8 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6, T7, T8>(tagset);

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
            where T9 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>(tagset);

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
            where T10 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(
                tagset
            );

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
            where T11 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<
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
            >(tagset);

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
            where T12 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<
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
            >(tagset);

        public GroupIndex GetSingleGroupWithTags(TagSet tagset) =>
            _queryEngine.GetSingleGroupWithTags(tagset);

        public GroupIndex GetSingleGroupWithTags<T1>()
            where T1 : struct, ITag => _queryEngine.GetSingleGroupWithTags<T1>();

        public GroupIndex GetSingleGroupWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => _queryEngine.GetSingleGroupWithTags<T1, T2>();

        public GroupIndex GetSingleGroupWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => _queryEngine.GetSingleGroupWithTags<T1, T2, T3>();

        public GroupIndex GetSingleGroupWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => _queryEngine.GetSingleGroupWithTags<T1, T2, T3, T4>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags(TagSet tagset) =>
            _queryEngine.GetGroupsWithTags(tagset);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1>()
            where T1 : struct, ITag => _queryEngine.GetGroupsWithTags<T1>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => _queryEngine.GetGroupsWithTags<T1, T2>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => _queryEngine.GetGroupsWithTags<T1, T2, T3>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => _queryEngine.GetGroupsWithTags<T1, T2, T3, T4>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(Type componentType) =>
            _queryEngine.GetGroupsWithComponents(componentType);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2
        ) => _queryEngine.GetGroupsWithComponents(componentType1, componentType2);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3
        ) => _queryEngine.GetGroupsWithComponents(componentType1, componentType2, componentType3);

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4
            );

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5
            );

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5,
                componentType6
            );

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5,
                componentType6,
                componentType7
            );

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3,
            Type componentType4,
            Type componentType5,
            Type componentType6,
            Type componentType7,
            Type componentType8
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5,
                componentType6,
                componentType7,
                componentType8
            );

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
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5,
                componentType6,
                componentType7,
                componentType8,
                componentType9
            );

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
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5,
                componentType6,
                componentType7,
                componentType8,
                componentType9,
                componentType10
            );

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
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5,
                componentType6,
                componentType7,
                componentType8,
                componentType9,
                componentType10,
                componentType11
            );

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
        ) =>
            _queryEngine.GetGroupsWithComponents(
                componentType1,
                componentType2,
                componentType3,
                componentType4,
                componentType5,
                componentType6,
                componentType7,
                componentType8,
                componentType9,
                componentType10,
                componentType11,
                componentType12
            );

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1>()
            where T1 : unmanaged, IEntityComponent => _queryEngine.GetGroupsWithComponents<T1>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5, T6>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6>();

        public ReadOnlyFastList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7>();

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
            where T8 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7, T8>();

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
            where T9 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>();

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
            where T10 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>();

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
            where T11 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>();

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
            where T12 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<
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
            >();

        public bool GroupHasComponent<T>(GroupIndex group)
            where T : IEntityComponent
        {
            var template = GetResolvedTemplateForGroup(group);
            return template.HasComponent<T>();
        }

        internal class GroupInfo
        {
            public GroupInfo(GroupIndex group, TagSet tagSet, ResolvedTemplate resolvedTemplate)
            {
                GroupIndex = group;
                TagSet = tagSet;
                ResolvedTemplate = resolvedTemplate;

                var tags = tagSet.Tags;
                var tagsSet = new DenseHashSet<Tag>(tags.Count);

                foreach (var tag in tags)
                {
                    tagsSet.Add(tag);
                }

                Tags = tagsSet;
            }

            public DenseHashSet<Tag> Tags { get; }
            public ResolvedTemplate ResolvedTemplate { get; }
            public GroupIndex GroupIndex { get; }
            public TagSet TagSet { get; }
        }
    }
}
