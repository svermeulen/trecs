using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Sets
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly SampleSettings _settings;
        readonly RenderableGameObjectManager _goManager;

        public SceneInitializer(
            World world,
            SampleSettings settings,
            RenderableGameObjectManager goManager
        )
        {
            _world = world.CreateAccessor(AccessorRole.Fixed);
            _settings = settings;
            _goManager = goManager;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(SetsPrefabs.Particle, CreateParticle);

            var offset = (_settings.GridSize - 1) * _settings.Spacing * 0.5f;

            for (int x = 0; x < _settings.GridSize; x++)
            {
                for (int z = 0; z < _settings.GridSize; z++)
                {
                    var position = new float3(
                        x * _settings.Spacing - offset,
                        0.5f,
                        z * _settings.Spacing - offset
                    );

                    _world.AddEntity<SampleTags.Particle>().Set(new Position(position));
                }
            }
        }

        GameObject CreateParticle()
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * _settings.BaseScale;
            return go;
        }
    }
}
