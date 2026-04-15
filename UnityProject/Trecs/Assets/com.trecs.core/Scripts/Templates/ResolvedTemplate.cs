using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
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

        public IReadOnlyList<Group> Groups { get; }

        public Template Template { get; }

        public string DebugName
        {
            get { return Template.DebugName; }
        }

        public IReadOnlyList<Template> AllBaseTemplates { get; }

        public override string ToString()
        {
            return Template.DebugName;
        }

        public TagSet AllTags { get; private set; }

        public IReadOnlyList<TagSet> States { get; }

        public IReadOnlyList<IResolvedComponentDeclaration> ComponentDeclarations { get; }

        // This includes all declarations, including inherited ones
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
