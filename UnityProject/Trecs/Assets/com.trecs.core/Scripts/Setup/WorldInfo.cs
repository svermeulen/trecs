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
    public sealed class WorldInfo
    {
        readonly TrecsLog _log;

        // Indexed by GroupIndex.Index. Every group registered in _allGroups
        // has a matching slot — pre-populated at construction, no null checks.
        readonly GroupInfo[] _groupInfos;
        readonly Dictionary<Template, FastList<GroupIndex>> _templateGroupsMap;
        readonly ReadOnlyFastList<GroupIndex> _allGroups;
        readonly Dictionary<TagSet, GroupIndex> _tagSetToIndex;
        readonly TagSet[] _indexToTagSet;
        readonly ReadOnlyFastList<ResolvedTemplate> _resolvedTemplates;
        readonly HashSet<Template> _resolvedTemplateSet = new();
        readonly HashSet<Template> _allTemplatesSet = new();
        readonly ReadOnlyFastList<Template> _allTemplates;
        readonly IReadOnlyList<EntitySet> _allSets;
        readonly ResolvedTemplate _globalTemplate;
        readonly WorldQueryEngine _queryEngine;

        const int _globalEntitySlotIndex = 0;

        readonly GroupIndex _globalGroup;
        readonly EntityIndex _globalEntityIndex;
        readonly ReadOnlyFastList<GroupIndex> _globalGroups;

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
                        // GroupIndex is 1-based; 0 is Null. Real groups occupy
                        // raw values 1..ushort.MaxValue, so at most 65535 real
                        // groups can exist (0-based indices 0..65534).
                        Assert.That(
                            indexToTagSet.Count < ushort.MaxValue,
                            "GroupIndex exhausted — world cannot have more than {} groups",
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
                        "Found multiple global templates.  There can only be one."
                    );
                    globalTemplate = resolvedTemplate;

                    Assert.That(
                        resolvedTemplate.Groups.Count == 1,
                        "Global template must only be in one group"
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
                        groupInfos[group.Index] == null,
                        "Found same group {} added multiple times.  Groups must be unique.",
                        group
                    );

                    groupInfos[group.Index] = new GroupInfo(group, groupTagSet, resolvedTemplate);

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
                    // A registered template's groups must be addressable
                    // unambiguously by tag set in single-group APIs
                    // (AddEntity<...>() and [FromWorld(typeof(Tag))] ->
                    // GroupIndex / NativeEntitySetIndices<TSet>). If a base
                    // template B is
                    // registered alongside a derived template D, every group of
                    // D contains B's tag set as a subset — so any query
                    // expressed only in B's tags would match groups from both
                    // templates.
                    //
                    // The resolver (WorldQueryEngine.GetSingleGroupWithTags)
                    // uses a same-template tiebreaker: when multiple groups
                    // match, it requires them all to belong to one registered
                    // template before picking the narrowest. Cross-template
                    // matches throw ambiguous by design — we never silently
                    // resolve between unrelated templates with overlapping tag
                    // sets, because that would turn "I forgot a tag" into a
                    // misrouted entity instead of an error.
                    //
                    // Catching the configuration here at world build is
                    // friendlier than failing at every AddEntity / [FromWorld]
                    // call site that touches an affected tag set.
                    // If a game genuinely wants both a base and a derived
                    // template to exist concretely (e.g. Orc + FlyingOrc), give
                    // each template a distinct discriminator tag (e.g.
                    // ITagged<Grounded> on the base, ITagged<Flying> on the
                    // derived) so their tag sets become siblings rather than
                    // strict subsets — then AddEntity queries resolve cleanly
                    // and this assertion no longer applies.
                    //
                    // Alternatives considered and rejected:
                    //  - Require AddEntity's tag set to exactly equal the
                    //    target group. Forces every spawner to know all of a
                    //    template's partition variants, blocking factory
                    //    patterns that legitimately don't.
                    //  - Auto-pick the closest match across template
                    //    boundaries (subset-order regardless of template).
                    //    Silently resolves real ambiguity, hiding
                    //    misconfigurations behind plausible-looking spawns.
                    Assert.That(
                        !_resolvedTemplateSet.Contains(baseType),
                        "Registered templates must not be base templates of other registered templates.  Found {} as a base template of {}",
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

            _groupInfos = groupInfos;
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
            Assert.That(!groupIndex.IsNull, "Cannot resolve Null GroupIndex to a TagSet");
            Assert.That(
                groupIndex.Index < _indexToTagSet.Length,
                "GroupIndex {} out of range [0, {})",
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
                Assert.That(enqueuedBaseTypes.Contains(baseType));
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
                componentDeclarationMap: new(allResolvedComponentDecMap),
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
            if (!group.IsNull && group.Index < _groupInfos.Length)
            {
                return _groupInfos[group.Index].Tags;
            }

            throw Assert.CreateException("Unrecognized group {}", group);
        }

        public Template GetSingleTemplateForTags(TagSet tags) =>
            _queryEngine.GetSingleTemplateForTags(tags);

        public ResolvedTemplate GetResolvedTemplateForGroup(GroupIndex group)
        {
            if (!group.IsNull && group.Index < _groupInfos.Length)
            {
                return _groupInfos[group.Index].ResolvedTemplate;
            }

            throw Assert.CreateException("No template found for group {}", group);
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
            // XOR by 0 is identity, so default(Tag).Guid==0 handles the
            // "no active variant" case without a branch.
            int newId = current.Id ^ activeVariant.Guid ^ replacement.Guid;
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
            int newId = current.Id ^ activeVariant.Guid;
            if (newId == 0)
                newId = 1;
            return new TagSet(newId);
        }

#if DEBUG && TRECS_INTERNAL_CHECKS
        static void AssertActiveVariantIsActuallyActive(TagSet current, Tag activeVariant)
        {
            if (activeVariant.Guid == 0)
                return;
            var tags = current.Tags;
            int count = tags.Count;
            for (int i = 0; i < count; i++)
            {
                if (tags[i].Guid == activeVariant.Guid)
                    return;
            }
            throw Assert.CreateException(
                "ReplaceDimensionTags / RemoveDimensionTags called with activeVariant {} that is not in current TagSet {} — XOR math would produce an unregistered TagSet id",
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
