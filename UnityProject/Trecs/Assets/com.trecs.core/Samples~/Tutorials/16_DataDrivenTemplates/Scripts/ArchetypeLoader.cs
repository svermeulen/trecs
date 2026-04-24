using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Internal;

namespace Trecs.Samples.DataDrivenTemplates
{
    /// <summary>
    /// Builds <see cref="Template"/> instances from <see cref="DataDrivenArchetype"/>
    /// definitions at runtime. Demonstrates that the <see cref="Template"/> and
    /// <see cref="ComponentDeclaration{T}"/> constructors are public and can be
    /// invoked without the source generator.
    /// </summary>
    public static class ArchetypeLoader
    {
        // Registries of component and tag types known at compile time. A data
        // file cannot invent new *types* — only new *compositions* of existing
        // ones. To expose a new component/tag to the data layer, add it here.
        static readonly Dictionary<string, Type> _componentTypes = new()
        {
            { nameof(Position), typeof(Position) },
            { nameof(Rotation), typeof(Rotation) },
            { nameof(UniformScale), typeof(UniformScale) },
            { nameof(ColorComponent), typeof(ColorComponent) },
            { nameof(GameObjectId), typeof(GameObjectId) },
            { nameof(OrbitParams), typeof(OrbitParams) },
            { nameof(BobParams), typeof(BobParams) },
        };

        static readonly Dictionary<string, Type> _tagTypes = new()
        {
            { nameof(SampleTags.Spinner), typeof(SampleTags.Spinner) },
            { nameof(SampleTags.Orbiter), typeof(SampleTags.Orbiter) },
            { nameof(SampleTags.Bobber), typeof(SampleTags.Bobber) },
        };

        public static IReadOnlyList<string> KnownComponentNames => _componentTypes.Keys.ToList();
        public static IReadOnlyList<string> KnownTagNames => _tagTypes.Keys.ToList();

        /// <summary>
        /// The result of building one archetype: the <see cref="Template"/> to
        /// register with <see cref="WorldBuilder"/>, and the <see cref="TagSet"/>
        /// to use at spawn time with <c>WorldAccessor.AddEntity(TagSet)</c>.
        /// </summary>
        public readonly struct BuiltArchetype
        {
            public readonly DataDrivenArchetype Source;
            public readonly Template Template;
            public readonly TagSet TagSet;
            public readonly IReadOnlyList<Type> ComponentTypes;

            public BuiltArchetype(
                DataDrivenArchetype source,
                Template template,
                TagSet tagSet,
                IReadOnlyList<Type> componentTypes
            )
            {
                Source = source;
                Template = template;
                TagSet = tagSet;
                ComponentTypes = componentTypes;
            }
        }

        public static List<BuiltArchetype> BuildAll(ArchetypeLibrary library)
        {
            var result = new List<BuiltArchetype>(library.Archetypes.Count);
            foreach (var archetype in library.Archetypes)
            {
                result.Add(Build(archetype));
            }
            return result;
        }

        public static BuiltArchetype Build(DataDrivenArchetype archetype)
        {
            var componentTypes = archetype.ComponentNames.Select(LookupComponent).ToList();
            var componentDeclarations = componentTypes.Select(CreateDeclaration).ToList();
            var tags = archetype.TagNames.Select(n => TagFactory.CreateTag(LookupTag(n))).ToList();

            var template = new Template(
                debugName: archetype.Name,
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: componentDeclarations,
                localTags: tags
            );

            return new BuiltArchetype(archetype, template, TagSet.FromTags(tags), componentTypes);
        }

        static Type LookupComponent(string name)
        {
            if (!_componentTypes.TryGetValue(name, out var type))
            {
                throw new InvalidOperationException(
                    $"Unknown component '{name}'. Register it in ArchetypeLoader._componentTypes. "
                        + $"Known components: {string.Join(", ", _componentTypes.Keys)}"
                );
            }
            return type;
        }

        static Type LookupTag(string name)
        {
            if (!_tagTypes.TryGetValue(name, out var type))
            {
                throw new InvalidOperationException(
                    $"Unknown tag '{name}'. Register it in ArchetypeLoader._tagTypes. "
                        + $"Known tags: {string.Join(", ", _tagTypes.Keys)}"
                );
            }
            return type;
        }

        // Constructs ComponentDeclaration<T> reflectively because T is only
        // known as a System.Type here. All eight nullable flag arguments are
        // passed as null — the resolver treats null as "use the default".
        static IComponentDeclaration CreateDeclaration(Type componentType)
        {
            var generic = typeof(ComponentDeclaration<>).MakeGenericType(componentType);
            return (IComponentDeclaration)
                Activator.CreateInstance(
                    generic,
                    new object[] { null, null, null, null, null, null, null, null }
                );
        }
    }
}
