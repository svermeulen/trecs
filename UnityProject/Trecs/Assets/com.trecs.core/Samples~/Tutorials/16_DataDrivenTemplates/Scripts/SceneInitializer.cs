using System;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.DataDrivenTemplates
{
    /// <summary>
    /// Spawns one entity per archetype defined in the <see cref="ArchetypeLibrary"/>.
    /// Each entity is created from a runtime-built <see cref="Template"/> and
    /// populated with the designer-authored initial values. Systems that read
    /// <see cref="Position"/>, <see cref="Rotation"/>, etc. work on these entities
    /// identically to source-generated ones.
    /// </summary>
    public class SceneInitializer
    {
        readonly World _world;
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly ArchetypeLoader.BuiltArchetype[] _archetypes;

        public SceneInitializer(
            World world,
            GameObjectRegistry gameObjectRegistry,
            ArchetypeLoader.BuiltArchetype[] archetypes
        )
        {
            _world = world;
            _gameObjectRegistry = gameObjectRegistry;
            _archetypes = archetypes;
        }

        public void Initialize()
        {
            var world = _world.CreateAccessor();

            for (int i = 0; i < _archetypes.Length; i++)
            {
                var built = _archetypes[i];
                var source = built.Source;

                var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
                go.name = source.Name;
                go.GetComponent<Renderer>().material.color = source.Color;

                // AddEntity(TagSet) is the non-generic spawn path. It accepts
                // any runtime-constructed TagSet whose combination was registered
                // as a Template via WorldBuilder.AddEntityType.
                var initializer = world.AddEntity(built.TagSet);

                foreach (var componentType in built.ComponentTypes)
                {
                    SetInitialValue(ref initializer, componentType, source, go);
                }

                initializer.AssertComplete();
            }
        }

        // Dispatches on a runtime Type to the correct compile-time Set<T> call.
        // Each known component type needs one case; this is the "glue" between
        // the data layer and the statically-typed Set<T> API.
        void SetInitialValue(
            ref EntityInitializer initializer,
            Type componentType,
            DataDrivenArchetype source,
            GameObject go
        )
        {
            if (componentType == typeof(Position))
            {
                initializer = initializer.Set<Position>(new(source.Position));
            }
            else if (componentType == typeof(Rotation))
            {
                initializer = initializer.Set<Rotation>(new(quaternion.identity));
            }
            else if (componentType == typeof(UniformScale))
            {
                initializer = initializer.Set<UniformScale>(new(source.Scale));
            }
            else if (componentType == typeof(ColorComponent))
            {
                initializer = initializer.Set<ColorComponent>(new(source.Color));
            }
            else if (componentType == typeof(GameObjectId))
            {
                initializer = initializer.Set(_gameObjectRegistry.Register(go));
            }
            else if (componentType == typeof(OrbitParams))
            {
                initializer = initializer.Set<OrbitParams>(
                    new()
                    {
                        Radius = source.OrbitParams.x,
                        Speed = source.OrbitParams.y,
                        Phase = source.OrbitParams.z,
                    }
                );
            }
            else if (componentType == typeof(BobParams))
            {
                initializer = initializer.Set<BobParams>(
                    new() { Amplitude = source.BobParams.x, Speed = source.BobParams.y }
                );
            }
            else
            {
                throw new InvalidOperationException(
                    $"SceneInitializer has no initial value for component type {componentType.Name}. "
                        + $"Add a case in SetInitialValue."
                );
            }
        }
    }
}
