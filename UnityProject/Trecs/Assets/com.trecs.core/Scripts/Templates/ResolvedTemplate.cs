using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// A fully resolved template with inherited components, tags, and groups materialized.
    /// Created during <see cref="WorldBuilder.Build"/> by flattening the template inheritance
    /// chain. Use <see cref="WorldInfo"/> to access resolved templates at runtime.
    /// </summary>
    public sealed class ResolvedTemplate
    {
        public ResolvedTemplate(
            Template template,
            IReadOnlyList<TagSet> groupTagSets,
            IReadOnlyList<Template> allBaseTemplates,
            IReadOnlyList<TagSet> partitions,
            IReadOnlyList<TagSet> dimensions,
            IReadOnlyList<IResolvedComponentDeclaration> componentDeclarations,
            ReadOnlyDenseDictionary<Type, IResolvedComponentDeclaration> componentDeclarationMap,
            IComponentBuilder[] componentBuilders,
            TagSet tagset,
            bool variableUpdateOnly
        )
        {
            Template = template;
            GroupTagSets = groupTagSets;
            ComponentDeclarations = componentDeclarations;
            ComponentDeclarationMap = componentDeclarationMap;
            ComponentBuilders = componentBuilders;
            Partitions = partitions;
            Dimensions = dimensions;
            AllTags = tagset;
            AllBaseTemplates = allBaseTemplates;
            VariableUpdateOnly = variableUpdateOnly;

            BuildDimensionCaches();
        }

        // Tag.Guid → (dim index in Dimensions, the dim's TagSet). Used by the
        // submission coalescer to resolve "which dim does this tag belong to"
        // in O(1) instead of an O(numDims × dimSize) scan. Built once at
        // construction; immutable thereafter.
        Dictionary<int, (int Index, TagSet Dim)> _tagToDim;

        // GroupTagSet.Id → array indexed by dim index → active variant Tag for
        // that dim in this group (default(Tag) means the dim has no active
        // variant in this group, i.e. presence/absence dim with the tag absent).
        // Lets the dim-replacement math avoid scanning the current TagSet for
        // the active variant. Keyed by id (not TagSet) because that's what the
        // submission coalescer carries; the entries are scoped to this
        // template's GroupTagSets so id collisions across templates don't matter.
        Dictionary<int, Tag[]> _activeVariantsByGroupTagSetId;

        void BuildDimensionCaches()
        {
            // Structural cap: coalescer's TouchedDimsMask is a ulong, so 64 dims
            // is the hard upper bound. Always-on, once-at-build — replaces what
            // used to be a runtime per-op assertion in EntitySubmitter.
            Assert.That(
                Dimensions.Count <= 64,
                "Template {} has {} partition dimensions, exceeding the 64-dim cap (TouchedDimsMask is a ulong)",
                DebugName,
                Dimensions.Count
            );

            _tagToDim = new Dictionary<int, (int, TagSet)>();
            for (int d = 0; d < Dimensions.Count; d++)
            {
                var dim = Dimensions[d];
                var dimTags = dim.Tags;
                for (int i = 0; i < dimTags.Count; i++)
                {
                    var t = dimTags[i];
#if DEBUG && TRECS_INTERNAL_CHECKS
                    Assert.That(
                        !_tagToDim.ContainsKey(t.Guid),
                        "Tag {} appears in two partition dimensions of template {} (existing dim {}, new dim {}). Each tag must belong to at most one dim.",
                        t,
                        DebugName,
                        _tagToDim.TryGetValue(t.Guid, out var existing) ? existing.Index : -1,
                        d
                    );
#endif
                    _tagToDim[t.Guid] = (d, dim);
                }
            }

            _activeVariantsByGroupTagSetId = new Dictionary<int, Tag[]>(GroupTagSets.Count);
            for (int g = 0; g < GroupTagSets.Count; g++)
            {
                var groupTagSet = GroupTagSets[g];
                var groupTags = groupTagSet.Tags;
                var active = new Tag[Dimensions.Count];
                for (int i = 0; i < groupTags.Count; i++)
                {
                    var t = groupTags[i];
                    if (_tagToDim.TryGetValue(t.Guid, out var info))
                    {
                        active[info.Index] = t;
                    }
                }
#if DEBUG && TRECS_INTERNAL_CHECKS
                Assert.That(
                    !_activeVariantsByGroupTagSetId.ContainsKey(groupTagSet.Id),
                    "Two GroupTagSets of template {} resolve to id {} — TagSet content-hash collision broke partition-variant uniqueness",
                    DebugName,
                    groupTagSet.Id
                );
#endif
                _activeVariantsByGroupTagSetId[groupTagSet.Id] = active;
            }
        }

        // Resolves which partition dimension <paramref name="tag"/> is a variant
        // of on this template. Returns false if the tag isn't part of any dim
        // (e.g. it's a base/component tag rather than a partition variant). O(1).
        internal bool TryGetDimForTag(Tag tag, out int dimIdx, out TagSet dim)
        {
            if (_tagToDim.TryGetValue(tag.Guid, out var info))
            {
                dimIdx = info.Index;
                dim = info.Dim;
                return true;
            }
            dimIdx = -1;
            dim = default;
            return false;
        }

        // True iff <paramref name="tagSet"/> is one of this template's
        // registered GroupTagSets. Used by submission-pipeline DEBUG checks
        // to catch XOR-math producing unregistered ids.
        internal bool IsRegisteredGroupTagSet(TagSet tagSet)
        {
            return _activeVariantsByGroupTagSetId.ContainsKey(tagSet.Id);
        }

        // Returns the variant of <paramref name="dimIdx"/> that is currently
        // active in the group identified by <paramref name="groupTagSet"/>.
        // Returns default(Tag) if no variant of that dim is active in this
        // group (only possible for presence/absence dims when the tag is
        // absent). DEBUG_INTERNAL_CHECKS produces a named Trecs exception on
        // miss; release falls through to the raw indexer (KeyNotFoundException).
        internal Tag GetActiveVariantInGroup(TagSet groupTagSet, int dimIdx)
        {
#if DEBUG && TRECS_INTERNAL_CHECKS
            if (!_activeVariantsByGroupTagSetId.TryGetValue(groupTagSet.Id, out var arr))
                throw Assert.CreateException(
                    "TagSet {} is not a registered group of template {} — coalescer invariant broken",
                    groupTagSet,
                    DebugName
                );
            return arr[dimIdx];
#else
            return _activeVariantsByGroupTagSetId[groupTagSet.Id][dimIdx];
#endif
        }

        /// <summary>
        /// True iff this template (or any of its ancestors) is declared
        /// <c>[VariableUpdateOnly]</c>. When true, every component on the template is
        /// VUO, Fixed-role queries that resolve to the template's groups are rejected,
        /// and the template's component arrays are skipped during the determinism
        /// checksum walk.
        /// </summary>
        public bool VariableUpdateOnly { get; }

        /// <summary>
        /// True iff <paramref name="dec"/> on this template is treated as VUO —
        /// either the field itself is declared <c>[VariableUpdateOnly]</c>, or
        /// the template (or any of its ancestors) is. Use this anywhere the rule
        /// is "is this access subject to the VUO restriction" rather than
        /// reading the two flags separately.
        /// </summary>
        public bool IsVariableUpdateOnly(IResolvedComponentDeclaration dec)
        {
            return dec.VariableUpdateOnly || VariableUpdateOnly;
        }

        /// <summary>
        /// Tag sets identifying the groups this template populates (one per valid
        /// partition combination). Used during world build to assign
        /// <see cref="GroupIndex"/>es.
        /// </summary>
        public IReadOnlyList<TagSet> GroupTagSets { get; }

        /// <summary>
        /// All groups this template populates (one per valid partition combination).
        /// Populated after world build — do not access before
        /// <see cref="WorldBuilder.Build"/> finishes.
        /// </summary>
        public IReadOnlyList<GroupIndex> Groups
        {
            get
            {
                Assert.IsNotNull(
                    _groups,
                    "ResolvedTemplate.Groups accessed before WorldBuilder.Build finished populating it"
                );
                return _groups;
            }
        }

        IReadOnlyList<GroupIndex> _groups;

        internal void SetGroups(IReadOnlyList<GroupIndex> groups)
        {
            Assert.IsNull(
                _groups,
                "ResolvedTemplate.Groups already initialized; SetGroups must be called exactly once per resolved template"
            );
            Assert.IsNotNull(groups);
            _groups = groups;
        }

        /// <summary>
        /// The original unresolved template definition.
        /// </summary>
        public Template Template { get; }

        public string DebugName
        {
            get { return Template.DebugName; }
        }

        /// <summary>
        /// All ancestor templates in the inheritance chain (transitive closure).
        /// </summary>
        public IReadOnlyList<Template> AllBaseTemplates { get; }

        public override string ToString()
        {
            return Template.DebugName;
        }

        /// <summary>
        /// Combined tag set from this template and all ancestors.
        /// </summary>
        public TagSet AllTags { get; private set; }

        /// <summary>
        /// Valid partition tag combinations (inherited from the unresolved template).
        /// </summary>
        public IReadOnlyList<TagSet> Partitions { get; }

        /// <summary>
        /// Partition dimensions (variants per dim) — inherited transitively from base
        /// templates. Used by tag-change operations (SetTag) to resolve which dim a
        /// tag belongs to and what its sibling variants are.
        /// </summary>
        public IReadOnlyList<TagSet> Dimensions { get; }

        /// <summary>
        /// All resolved component declarations (local and inherited).
        /// </summary>
        public IReadOnlyList<IResolvedComponentDeclaration> ComponentDeclarations { get; }

        /// <summary>
        /// Component builders for all declarations (local and inherited).
        /// </summary>
        public IComponentBuilder[] ComponentBuilders { get; }

        public ReadOnlyDenseDictionary<
            Type,
            IResolvedComponentDeclaration
        > ComponentDeclarationMap { get; }

        public bool HasComponent<T>()
            where T : IEntityComponent
        {
            return ComponentDeclarationMap.ContainsKey(typeof(T));
        }

        public bool HasComponent(Type type)
        {
            return ComponentDeclarationMap.ContainsKey(type);
        }

        public IResolvedComponentDeclaration TryGetComponentDeclaration<T>()
            where T : unmanaged, IEntityComponent
        {
            return TryGetComponentDeclaration(typeof(T));
        }

        public IResolvedComponentDeclaration TryGetComponentDeclaration(Type componentType)
        {
            return ComponentDeclarationMap.TryGetValue(componentType, out var declaration)
                ? declaration
                : null;
        }

        public IResolvedComponentDeclaration GetComponentDeclaration(Type componentType)
        {
            if (ComponentDeclarationMap.TryGetValue(componentType, out var dec))
            {
                return dec;
            }

            throw Assert.CreateException(
                "Expected to find component declaration for type {} on entity {} but was not found",
                componentType,
                Template.DebugName
            );
        }

        public IResolvedComponentDeclaration GetComponentDeclaration<T>()
            where T : unmanaged, IEntityComponent
        {
            return GetComponentDeclaration(typeof(T));
        }
    }
}
