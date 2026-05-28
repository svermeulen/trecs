using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Read-only metadata about all registered templates, groups, components, and tags in a world.
    /// Use to discover which groups exist, resolve tag sets to groups, and inspect component layouts.
    /// Accessed via <see cref="WorldAccessor.WorldInfo"/> or <see cref="World.WorldInfo"/>.
    /// </summary>
    public sealed class WorldInfo : IDisposable
    {
        readonly TrecsLog _log;

        // Indexed by GroupIndex.Index. Every group registered in _allGroups
        // has a matching slot — pre-populated at construction, no null checks.
        readonly GroupInfo[] _groupInfos;
        readonly IterableDictionary<RefKey<Template>, List<GroupIndex>> _templateGroupsMap;
        readonly ReadOnlyList<GroupIndex> _allGroups;
        readonly IterableDictionary<TagSet, GroupIndex> _tagSetToIndex;
        readonly TagSet[] _indexToTagSet;
        readonly ReadOnlyList<ResolvedTemplate> _resolvedTemplates;
        readonly HashSet<Template> _resolvedTemplateSet = new();
        readonly HashSet<Template> _allTemplatesSet = new();
        readonly ReadOnlyList<Template> _allTemplates;
        readonly IReadOnlyList<EntitySet> _allSets;
        readonly ResolvedTemplate _globalTemplate;
        readonly WorldQueryEngine _queryEngine;

        const int _globalEntitySlotIndex = 0;

        readonly GroupIndex _globalGroup;
        readonly EntityIndex _globalEntityIndex;
        readonly ReadOnlyList<GroupIndex> _globalGroups;

        // Native-side mirror of the permissive TagSet→GroupIndex resolution for
        // Burst-job consumption. Built once in the constructor and read-only for
        // the world's lifetime. Contains both:
        //   (a) every group's exact (full) tag set as a key → that group, and
        //   (b) every partial subset of any group's tag set that uniquely resolves
        //       via WorldQueryEngine.GetSingleGroupWithTags.
        // Burst-path AddEntity does the same lookup managed AddEntity does, without
        // going through managed code.
        NativeHashMap<int, GroupIndex> _tagSetIdToGroupNative;

        // Per-group component layout: slot size, per-component offsets and sizes, and
        // default-bytes prototype. Built once and read-only for the world's lifetime.
        // Consumed by the Burst-friendly AddEntity fast path and the parallel-fill
        // drain pipeline.
        WorldComponentLayouts _componentLayouts;
        bool _isDisposed;

        public WorldInfo(
            TrecsLog log,
            IReadOnlyList<Template> templatesList,
            IReadOnlyList<EntitySet> sets
        )
        {
            _log = log;
            _allSets = sets ?? Array.Empty<EntitySet>();
            if (!HasGlobalsTemplate(templatesList))
            {
                templatesList = new List<Template>(templatesList)
                {
                    TrecsTemplates.Globals.Template,
                };
            }

            var resolvedTemplatesList = new List<ResolvedTemplate>(templatesList.Count);

            foreach (var template in templatesList)
            {
                resolvedTemplatesList.Add(ResolveTemplate(template));
            }

            // Discard any explicitly-registered template whose Template object
            // appears as a base of another registered template.  A base
            // template's tag set is always a strict subset of its derived
            // template's, so AddEntity queries expressed in the base's tags
            // would match groups from both — an ambiguity that can't be
            // resolved without a discriminator tag.  Rather than forcing users
            // to remember to register only leaves, we silently prune the
            // redundant bases here (the derived templates already carry the
            // base's components, tags, and groups via IExtends).
            var basesToRemove = new HashSet<ResolvedTemplate>();
            foreach (var rt in resolvedTemplatesList)
            {
                foreach (var other in resolvedTemplatesList)
                {
                    if (ReferenceEquals(other, rt))
                    {
                        continue;
                    }

                    if (other.AllBaseTemplates.Contains(rt.Template))
                    {
                        basesToRemove.Add(rt);
                        break;
                    }
                }
            }

            foreach (var rt in basesToRemove)
            {
                _log.Warning(
                    "Template {0} is a base of another registered template and was automatically removed. Register only leaf templates; base templates are discovered via IExtends.",
                    rt.DebugName
                );
            }

            resolvedTemplatesList.RemoveAll(basesToRemove.Contains);

            // Pass 1: assign a sequential GroupIndex to each unique TagSet that
            // appears as a group on any resolved template.
            var tagSetToIndex = new IterableDictionary<TagSet, GroupIndex>();
            var indexToTagSet = new List<TagSet>();

            foreach (var resolvedTemplate in resolvedTemplatesList)
            {
                foreach (var tagSet in resolvedTemplate.GroupTagSets)
                {
                    if (!tagSetToIndex.ContainsKey(tagSet))
                    {
                        // GroupIndex is 1-based; 0 is Null. Real groups occupy
                        // raw values 1..ushort.MaxValue, so at most 65535 real
                        // groups can exist (0-based indices 0..65534).
                        TrecsDebugAssert.That(
                            indexToTagSet.Count < ushort.MaxValue,
                            "GroupIndex exhausted — world cannot have more than {0} groups",
                            ushort.MaxValue
                        );
                        var idx = GroupIndex.FromIndex(indexToTagSet.Count);
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
                resolvedTemplate.SetGroups(groups);
            }

            // Sized to the registry above; every registered GroupIndex gets a slot.
            var groupInfos = new GroupInfo[indexToTagSet.Count];
            var templateGroupsMap = new IterableDictionary<RefKey<Template>, List<GroupIndex>>();
            var allGroups = new List<GroupIndex>();
            var allTemplates = new List<Template>();

            void AddGroupToTemplate(Template template, GroupIndex group)
            {
                if (templateGroupsMap.TryGetValue(template, out var existingGroups))
                {
                    existingGroups.Add(group);
                }
                else
                {
                    templateGroupsMap.Add(template, new List<GroupIndex> { group });
                }
            }

            ResolvedTemplate globalTemplate = null;
            GroupIndex? globalGroup = null;

            foreach (var resolvedTemplate in resolvedTemplatesList)
            {
                TrecsDebugAssert.That(
                    resolvedTemplate.AllTags.Tags.Count > 0,
                    "Template {0} must have at least one tag",
                    resolvedTemplate.DebugName
                );

                if (
                    resolvedTemplate.Template == TrecsTemplates.Globals.Template
                    || resolvedTemplate.AllBaseTemplates.Contains(TrecsTemplates.Globals.Template)
                )
                {
                    TrecsDebugAssert.That(
                        globalTemplate == null,
                        "Found multiple global templates.  There can only be one."
                    );
                    globalTemplate = resolvedTemplate;

                    TrecsDebugAssert.That(
                        resolvedTemplate.Groups.Count == 1,
                        "Global template must only be in one group"
                    );
                    TrecsDebugAssert.That(!globalGroup.HasValue);
                    globalGroup = resolvedTemplate.Groups[0];
                }

                for (int i = 0; i < resolvedTemplate.Groups.Count; i++)
                {
                    var group = resolvedTemplate.Groups[i];
                    var groupTagSet = resolvedTemplate.GroupTagSets[i];

                    TrecsDebugAssert.That(
                        groupInfos[group.Index] == null,
                        "Found same group {0} added multiple times. Groups must be unique.",
                        group
                    );

                    groupInfos[group.Index] = new GroupInfo(group, groupTagSet, resolvedTemplate);

                    AddGroupToTemplate(resolvedTemplate.Template, group);

                    foreach (var baseTemplate in resolvedTemplate.AllBaseTemplates)
                    {
                        AddGroupToTemplate(baseTemplate, group);
                    }

                    allGroups.Add(group);
                }

                var wasAdded = _resolvedTemplateSet.Add(resolvedTemplate.Template);
                TrecsDebugAssert.That(wasAdded);
            }

            foreach (var resolvedTemplate in resolvedTemplatesList)
            {
                if (_allTemplatesSet.Add(resolvedTemplate.Template))
                {
                    allTemplates.Add(resolvedTemplate.Template);
                }

                foreach (var baseType in resolvedTemplate.AllBaseTemplates)
                {
                    TrecsDebugAssert.That(!_resolvedTemplateSet.Contains(baseType));

                    if (_allTemplatesSet.Add(baseType))
                    {
                        allTemplates.Add(baseType);
                    }
                }
            }

            TrecsDebugAssert.IsNotNull(globalTemplate);

            _globalTemplate = globalTemplate;
            _globalGroup = globalGroup.Value;
            _globalGroups = new List<GroupIndex> { globalGroup.Value };
            _globalEntityIndex = new(_globalEntitySlotIndex, _globalGroup);

            _groupInfos = groupInfos;
            _templateGroupsMap = templateGroupsMap;
            _allGroups = allGroups;
            _resolvedTemplates = resolvedTemplatesList;
            _allTemplates = allTemplates;

            _indexToTagSet = indexToTagSet.ToArray();
            _tagSetToIndex = tagSetToIndex;

            _queryEngine = new WorldQueryEngine(_allGroups, _groupInfos, this);

            BuildTagSetIdToGroupNative();
            _componentLayouts = new WorldComponentLayouts(_allGroups, this);
        }

        // Maximum tags-per-group supported by the fast-path subset enumeration.
        // The enumeration explores 2^N subsets per group, so this is also the
        // upper bound on per-group enumeration cost. Far above realistic Trecs
        // template tag counts (typically ≤6 including partition variants).
        const int MaxTagsPerGroupForFastPath = 16;

        // Populates _tagSetIdToGroupNative with two layers:
        //   (1) every group's exact (full) tag set as a key → that group
        //   (2) every partial subset of any group's tag set that uniquely resolves
        //       via the managed permissive resolver (WorldQueryEngine.GetSingleGroupWithTags)
        // The map thus accepts the same set of TagSets the managed resolver does, so
        // the Burst-path AddEntity can resolve any TagSet a caller could legally pass.
        //
        // Cost is O(|groups| × 2^|tags-per-group|) resolver calls at world init.
        // Realistic Trecs templates have ≤6 tags per group, putting this well under
        // a millisecond in practice. The cap above asserts to catch pathological cases
        // before they blow up init time.
        void BuildTagSetIdToGroupNative()
        {
            _tagSetIdToGroupNative = new NativeHashMap<int, GroupIndex>(
                _tagSetToIndex.Count * 4,
                Allocator.Persistent
            );

            // Pass 1: exact (full) tag sets.
            foreach (var (tagSet, group) in _tagSetToIndex)
            {
                _tagSetIdToGroupNative.Add(tagSet.Id, group);
            }

            // Pass 2: partial subsets that uniquely resolve to a single group.
            var scratch = new List<Tag>(MaxTagsPerGroupForFastPath);
            foreach (var (fullTagSet, group) in _tagSetToIndex)
            {
                var tags = fullTagSet.Tags;
                int n = tags.Count;

                TrecsDebugAssert.That(
                    n <= MaxTagsPerGroupForFastPath,
                    "Group {0} has {1} tags, exceeding the fast-path subset-enumeration cap of {2}. "
                        + "Raise MaxTagsPerGroupForFastPath if this is intentional, but be aware "
                        + "init cost grows as 2^N per group.",
                    group,
                    n,
                    MaxTagsPerGroupForFastPath
                );

                // Skip mask=0 (empty set) and mask=(1<<n)-1 (full set, already added).
                int fullMask = (1 << n) - 1;
                for (int mask = 1; mask < fullMask; mask++)
                {
                    scratch.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        if ((mask & (1 << i)) != 0)
                            scratch.Add(tags[i]);
                    }
                    var subset = TagSet.FromTags(scratch);

                    // Already in the map (some other group's exact match, or a subset
                    // already claimed in an earlier group's iteration).
                    if (_tagSetIdToGroupNative.ContainsKey(subset.Id))
                        continue;

                    // Try* form avoids the throw-and-unwind cost on the "ambiguous"
                    // and "no match" paths, which are the common case as we sweep
                    // 2^n subsets per group.
                    if (
                        _queryEngine.TryGetSingleGroupWithTags(subset, out var resolved)
                        && resolved == group
                    )
                    {
                        _tagSetIdToGroupNative.Add(subset.Id, group);
                    }
                }
            }
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

            throw TrecsDebugAssert.CreateException("No group registered for tag set {0}", tagSet);
        }

        /// <summary>
        /// Returns the <see cref="TagSet"/> associated with a <see cref="GroupIndex"/>.
        /// </summary>
        public TagSet ToTagSet(GroupIndex groupIndex)
        {
            TrecsDebugAssert.That(!groupIndex.IsNull, "Cannot resolve Null GroupIndex to a TagSet");
            TrecsDebugAssert.That(
                groupIndex.Index < _indexToTagSet.Length,
                "GroupIndex {0} out of range [0, {1})",
                groupIndex.Index,
                _indexToTagSet.Length
            );
            return _indexToTagSet[groupIndex.Index];
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

        ResolvedTemplate ResolveTemplate(Template template)
        {
            _log.Trace("Building entity with name {0}", template.DebugName);

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
                TrecsDebugAssert.That(enqueuedBaseTypes.Contains(baseType));
                allBaseTypesList.Add(baseType);
                _log.Trace(
                    "Added base type {0} to entity {1}",
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

            var allComponentDecMap =
                new IterableDictionary<RefKey<Type>, List<IComponentDeclaration>>();

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
                new IterableDictionary<RefKey<Type>, IResolvedComponentDeclaration>();

            foreach (var (componentType, componentDecs) in allComponentDecMap)
            {
                TrecsDebugAssert.That(componentDecs.Count > 0);

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
                TrecsDebugAssert.That(
                    baseType.Partitions.IsEmpty(),
                    "Partitions must only be specified on the concrete template, not on a base template"
                );
            }

            var allTags = new List<Tag>();
            var allPartitions = new List<TagSet>();
            var allDimensions = new List<TagSet>();

            allPartitions.AddRange(template.Partitions);
            allDimensions.AddRange(template.Dimensions);
            allTags.AddRange(template.LocalTags);

            foreach (var baseType in allBaseTypesList)
            {
                allTags.AddRange(baseType.LocalTags);
                allPartitions.AddRange(baseType.Partitions);
                allDimensions.AddRange(baseType.Dimensions);
            }

            var tagset = TagSet.FromTags(allTags);

            // VariableUpdateOnly is inherited transitively: if the concrete
            // template OR any of its ancestors is locally declared VUO, the
            // resolved template is VUO. There's no "un-declare via
            // inheritance" — once any node in the chain opts in, every
            // descendant is VUO.
            bool variableUpdateOnly =
                template.LocalVariableUpdateOnly
                || allBaseTypesList.Any(b => b.LocalVariableUpdateOnly);

            return new(
                template: template,
                groupTagSets: CalculateTemplateGroupTagSets(tagset, allPartitions),
                allBaseTemplates: allBaseTypesList,
                partitions: allPartitions,
                dimensions: allDimensions,
                componentDeclarations: allComponentDecs,
                componentDeclarationMap: allResolvedComponentDecMap,
                componentBuilders: componentBuilders.ToArray(),
                tagset: tagset,
                variableUpdateOnly: variableUpdateOnly
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

        internal WorldQueryEngine QueryEngine => _queryEngine;

        public ReadOnlyList<ResolvedTemplate> ResolvedTemplates
        {
            get { return _resolvedTemplates; }
        }

        public GroupIndex GlobalGroup
        {
            get { return _globalGroup; }
        }

        public ReadOnlyList<GroupIndex> GlobalGroups
        {
            get { return _globalGroups; }
        }

        public ReadOnlyList<GroupIndex> AllGroups
        {
            get { return _allGroups; }
        }

        public ReadOnlyList<Template> AllTemplates
        {
            get { return _allTemplates; }
        }

        public IReadOnlyList<EntitySet> AllSets
        {
            get { return _allSets; }
        }

        public EntityIndex GlobalEntityIndex
        {
            get { return _globalEntityIndex; }
        }

        public ResolvedTemplate GlobalTemplate
        {
            get { return _globalTemplate; }
        }

        public ReadOnlyList<GroupIndex> GetTemplateGroups(Template template)
        {
            if (_templateGroupsMap.TryGetValue(template, out var groups))
            {
                return groups;
            }

            throw TrecsDebugAssert.CreateException("No groups found for template {0}", template);
        }

        public ReadOnlyIterableHashSet<Tag> GetGroupTags(GroupIndex group)
        {
            if (!group.IsNull && group.Index < _groupInfos.Length)
            {
                return _groupInfos[group.Index].Tags;
            }

            throw TrecsDebugAssert.CreateException("Unrecognized group {0}", group);
        }

        public Template GetSingleTemplateForTags(TagSet tags) =>
            _queryEngine.GetSingleTemplateForTags(tags);

        public ResolvedTemplate GetResolvedTemplateForGroup(GroupIndex group)
        {
            if (!group.IsNull && group.Index < _groupInfos.Length)
            {
                return _groupInfos[group.Index].ResolvedTemplate;
            }

            throw TrecsDebugAssert.CreateException("No template found for group {0}", group);
        }

        public ResolvedTemplate GetResolvedTemplateForTags(TagSet tags) =>
            _queryEngine.GetResolvedTemplateForTags(tags);

        // Returns a TagSet equal to <paramref name="current"/> with the dim
        // identified by <paramref name="activeVariant"/> replaced by
        // <paramref name="replacement"/>. <paramref name="activeVariant"/> is
        // the dim's currently-active variant in <paramref name="current"/>, or
        // default(Tag) if the dim has no active variant (presence/absence dim
        // with tag absent). Computes the new id by XOR — caller is responsible
        // for resolving the active variant via
        // <see cref="ResolvedTemplate.GetActiveVariantInGroup"/>. Mirrors
        // TagSetRegistry's id==0→1 normalization.
        internal static TagSet ReplaceDimensionTags(
            TagSet current,
            Tag activeVariant,
            Tag replacement
        )
        {
#if DEBUG && TRECS_INTERNAL_CHECKS
            AssertActiveVariantIsActuallyActive(current, activeVariant);
#endif
            // XOR by 0 is identity, so default(Tag).Value==0 handles the
            // "no active variant" case without a branch.
            int newId = current.Id ^ activeVariant.Value ^ replacement.Value;
            if (newId == 0)
                newId = 1;
            return new TagSet(newId);
        }

        // Returns a TagSet equal to <paramref name="current"/> with the dim's
        // currently-active variant stripped (no-op if no active variant).
        // XOR-direct — see ReplaceDimensionTags for the contract.
        internal static TagSet RemoveDimensionTags(TagSet current, Tag activeVariant)
        {
#if DEBUG && TRECS_INTERNAL_CHECKS
            AssertActiveVariantIsActuallyActive(current, activeVariant);
#endif
            int newId = current.Id ^ activeVariant.Value;
            if (newId == 0)
                newId = 1;
            return new TagSet(newId);
        }

#if DEBUG && TRECS_INTERNAL_CHECKS
        static void AssertActiveVariantIsActuallyActive(TagSet current, Tag activeVariant)
        {
            if (activeVariant.Value == 0)
                return;
            var tags = current.Tags;
            int count = tags.Count;
            for (int i = 0; i < count; i++)
            {
                if (tags[i].Value == activeVariant.Value)
                    return;
            }
            throw TrecsDebugAssert.CreateException(
                "ReplaceDimensionTags / RemoveDimensionTags called with activeVariant {0} that is not in current TagSet {1} — XOR math would produce an unregistered TagSet id",
                activeVariant,
                current
            );
        }
#endif

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

        public ReadOnlyList<GroupIndex> CommonGetTaggedGroupsWithComponents(
            TagSet tagset,
            List<Type> componentTypes
        ) => _queryEngine.CommonGetTaggedGroupsWithComponents(tagset, componentTypes);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<T1>(TagSet tagset)
            where T1 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1>(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2>(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2>(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3>(TagSet tagset)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3>(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4>(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5>(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6>(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6, T7>(
            TagSet tagset
        )
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithTagsAndComponents<T1, T2, T3, T4, T5, T6, T7>(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithTagsAndComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithTags(TagSet tagset) =>
            _queryEngine.GetGroupsWithTags(tagset);

        public ReadOnlyList<GroupIndex> GetGroupsWithTags<T1>()
            where T1 : struct, ITag => _queryEngine.GetGroupsWithTags<T1>();

        public ReadOnlyList<GroupIndex> GetGroupsWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => _queryEngine.GetGroupsWithTags<T1, T2>();

        public ReadOnlyList<GroupIndex> GetGroupsWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => _queryEngine.GetGroupsWithTags<T1, T2, T3>();

        public ReadOnlyList<GroupIndex> GetGroupsWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => _queryEngine.GetGroupsWithTags<T1, T2, T3, T4>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(Type componentType) =>
            _queryEngine.GetGroupsWithComponents(componentType);

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2
        ) => _queryEngine.GetGroupsWithComponents(componentType1, componentType2);

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
            Type componentType1,
            Type componentType2,
            Type componentType3
        ) => _queryEngine.GetGroupsWithComponents(componentType1, componentType2, componentType3);

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents(
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1>()
            where T1 : unmanaged, IEntityComponent => _queryEngine.GetGroupsWithComponents<T1>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5, T6>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7, T8>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
            where T6 : unmanaged, IEntityComponent
            where T7 : unmanaged, IEntityComponent
            where T8 : unmanaged, IEntityComponent =>
            _queryEngine.GetGroupsWithComponents<T1, T2, T3, T4, T5, T6, T7, T8>();

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<
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

        public ReadOnlyList<GroupIndex> GetGroupsWithComponents<
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

        /// <summary>
        /// Burst-readable lookup of <see cref="TagSet"/>.Id → <see cref="GroupIndex"/>.
        /// Built once at world construction; read-only for the world's lifetime. Contains
        /// both every group's exact (full) tag set and every partial subset that uniquely
        /// resolves to a single group via <see cref="GetSingleGroupWithTags(TagSet)"/>,
        /// so any TagSet a managed caller could pass to AddEntity also works from Burst.
        /// </summary>
        internal NativeHashMap<int, GroupIndex> TagSetIdToGroupNative => _tagSetIdToGroupNative;

        /// <summary>
        /// Per-group component layout metadata (slot sizes, offsets, default bytes).
        /// Read-only for the world's lifetime. Consumed by the Burst-friendly AddEntity
        /// fast path and the parallel-fill drain pipeline.
        /// </summary>
        internal WorldComponentLayouts ComponentLayouts => _componentLayouts;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            if (_tagSetIdToGroupNative.IsCreated)
            {
                _tagSetIdToGroupNative.Dispose();
            }
            _componentLayouts?.Dispose();
            _isDisposed = true;
        }

        internal class GroupInfo
        {
            public GroupInfo(GroupIndex group, TagSet tagSet, ResolvedTemplate resolvedTemplate)
            {
                GroupIndex = group;
                TagSet = tagSet;
                ResolvedTemplate = resolvedTemplate;

                var tags = tagSet.Tags;
                var tagsSet = new IterableHashSet<Tag>(tags.Count);

                foreach (var tag in tags)
                {
                    tagsSet.Add(tag);
                }

                Tags = tagsSet;
            }

            public IterableHashSet<Tag> Tags { get; }
            public ResolvedTemplate ResolvedTemplate { get; }
            public GroupIndex GroupIndex { get; }
            public TagSet TagSet { get; }
        }
    }
}
