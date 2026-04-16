using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Interpolation
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly int _entitiesPerRing;
        readonly float _orbitRadius;
        readonly float _orbitSpeed;

        public SceneInitializer(
            World world,
            GameObjectRegistry gameObjectRegistry,
            int entitiesPerRing,
            float orbitRadius,
            float orbitSpeed
        )
        {
            _world = world.CreateAccessor();
            _gameObjectRegistry = gameObjectRegistry;
            _entitiesPerRing = entitiesPerRing;
            _orbitRadius = orbitRadius;
            _orbitSpeed = orbitSpeed;
        }

        public void Initialize()
        {
            const float ringOffsetX = 5f;

            for (int i = 0; i < _entitiesPerRing; i++)
            {
                // Left ring: smooth (interpolated)
                CreateOrbitEntity<OrbitTags.Smooth>(
                    i,
                    -ringOffsetX,
                    "Smooth",
                    Color.green,
                    interpolated: true
                );
                // Right ring: raw (not interpolated)
                CreateOrbitEntity<OrbitTags.Raw>(
                    i,
                    ringOffsetX,
                    "Raw",
                    Color.red,
                    interpolated: false
                );
            }
        }

        void CreateOrbitEntity<TTag>(
            int index,
            float centerX,
            string label,
            Color color,
            bool interpolated
        )
            where TTag : struct, ITag
        {
            float phase = (float)index / _entitiesPerRing * 2f * math.PI;

            var position = new float3(
                centerX + math.cos(phase) * _orbitRadius,
                0.5f,
                math.sin(phase) * _orbitRadius
            );

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"{label}_{index}";
            go.transform.position = (Vector3)position;
            go.transform.localScale = Vector3.one * 0.6f;
            go.GetComponent<Renderer>().material.color = color;

            var entity = _world.AddEntity<TTag>();

            // SetInterpolated sets Position, Interpolated<Position>,
            // and InterpolatedPrevious<Position> all at once.
            if (interpolated)
                entity.SetInterpolated(new Position(position));
            else
                entity.Set(new Position(position));

            entity
                .Set(
                    new OrbitParams
                    {
                        Radius = _orbitRadius,
                        Speed = _orbitSpeed,
                        Phase = phase,
                        CenterX = centerX,
                    }
                )
                .Set(_gameObjectRegistry.Register(go))
                .AssertComplete();
        }
    }
}
