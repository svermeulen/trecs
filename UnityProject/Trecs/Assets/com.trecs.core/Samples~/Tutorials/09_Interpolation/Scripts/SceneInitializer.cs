using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Interpolation
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly int _entitiesPerRing;
        readonly float _orbitRadius;
        readonly float _orbitSpeed;
        readonly RenderableGameObjectManager _goManager;

        public SceneInitializer(
            World world,
            int entitiesPerRing,
            float orbitRadius,
            float orbitSpeed,
            RenderableGameObjectManager goManager
        )
        {
            _world = world.CreateAccessor(AccessorRole.Fixed);
            _entitiesPerRing = entitiesPerRing;
            _orbitRadius = orbitRadius;
            _orbitSpeed = orbitSpeed;
            _goManager = goManager;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(
                InterpolationPrefabs.SmoothCube,
                () => CreateCube(Color.green)
            );
            _goManager.RegisterFactory(InterpolationPrefabs.RawCube, () => CreateCube(Color.red));

            const float ringOffsetX = 5f;

            for (int i = 0; i < _entitiesPerRing; i++)
            {
                // Left ring: smooth (interpolated)
                CreateOrbitEntity<OrbitTags.Smooth>(i, -ringOffsetX, interpolated: true);
                // Right ring: raw (not interpolated)
                CreateOrbitEntity<OrbitTags.Raw>(i, ringOffsetX, interpolated: false);
            }
        }

        void CreateOrbitEntity<TTag>(int index, float centerX, bool interpolated)
            where TTag : struct, ITag
        {
            float phase = (float)index / _entitiesPerRing * 2f * math.PI;

            var position = new float3(
                centerX + math.cos(phase) * _orbitRadius,
                0.5f,
                math.sin(phase) * _orbitRadius
            );

            var entity = _world.AddEntity<TTag>();

            if (interpolated)
            {
                entity.SetInterpolated(new Position(position));
                entity.SetInterpolated(new Rotation(quaternion.identity));
            }
            else
            {
                entity.Set(new Position(position));
                entity.Set(new Rotation(quaternion.identity));
            }

            entity.Set(
                new OrbitParams
                {
                    Radius = _orbitRadius,
                    Speed = _orbitSpeed,
                    Phase = phase,
                    CenterX = centerX,
                }
            );
        }

        static GameObject CreateCube(Color color)
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Cube);
            go.transform.localScale = Vector3.one * 0.6f;
            go.GetComponent<Renderer>().material.color = color;
            return go;
        }
    }
}
