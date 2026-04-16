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
    public class ResolvedTemplate
    {
        public ResolvedTemplate(
            Template template,
            IReadOnlyList<Group> groups,
            IReadOnlyList<Template> allBaseTemplates,
            IReadOnlyList<TagSet> states,
            IReadOnlyList<IResolvedComponentDeclaration> componentDeclarations,
            ReadOnlyDenseDictionary<Type, IResolvedComponentDeclaration> componentDeclarationMap,
            IComponentBuilder[] componentBuilders,
            TagSet tagset
        )
        {
            Template = template;
            Groups = groups;
            ComponentDeclarations = componentDeclarations;
            ComponentDeclarationMap = componentDeclarationMap;
            ComponentBuilders = componentBuilders;
            States = states;
            AllTags = tagset;
            AllBaseTemplates = allBaseTemplates;
        }

        /// <summary>
        /// All groups this template populates (one per valid state combination).
        /// </summary>
        public IReadOnlyList<Group> Groups { get; }

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
        /// Valid state tag combinations (inherited from the unresolved template).
        /// </summary>
        public IReadOnlyList<TagSet> States { get; }

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

        public ComponentDeclaration<T> TryGetComponentDeclaration<T>()
            where T : unmanaged, IEntityComponent
        {
            return ComponentDeclarationMap.TryGetValue(typeof(T), out var declaration)
                ? (ComponentDeclaration<T>)declaration
                : null;
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

        public ComponentDeclaration<T> GetComponentDeclaration<T>()
            where T : unmanaged, IEntityComponent
        {
            return (ComponentDeclaration<T>)GetComponentDeclaration(typeof(T));
        }
    }
}
