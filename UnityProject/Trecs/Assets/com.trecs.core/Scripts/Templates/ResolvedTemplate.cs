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
            AllTags = tagset;
            AllBaseTemplates = allBaseTemplates;
            VariableUpdateOnly = variableUpdateOnly;
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
