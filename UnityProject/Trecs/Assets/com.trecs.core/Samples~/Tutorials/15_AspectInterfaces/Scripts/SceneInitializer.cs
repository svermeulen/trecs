using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Trecs.Samples.AspectInterfaces
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
            _goManager.RegisterFactory(
                AspectInterfacesPrefabs.Boss,
                () => CreateCube(_settings.BossScale)
            );
            _goManager.RegisterFactory(
                AspectInterfacesPrefabs.Enemy,
                () => CreateCube(_settings.EnemyScale)
            );

            // One boss at the origin. Every per-entity stat is pulled
            // from SampleSettings so the scene inspector is the single
            // source of truth.
            var bossPos = float3.zero;
            _world
                .AddEntity<SampleTags.Boss>()
                .Set(new Position(bossPos))
                .Set(new Armor { Value = _settings.BossArmor })
                .Set(new MaxHealth { Value = _settings.BossMaxHealth })
                .Set(new Health { Value = _settings.BossMaxHealth })
                .Set(new ColorComponent { Value = _settings.BossBaseColor });

            // Enemies arranged on a ring around the boss. Each gets a
            // different armor value so per-entity flee cadence visibly
            // differs. EnemyArmors.Length drives the enemy count.
            int count = _settings.EnemyArmors.Length;
            var rng = new Random(0x9E3779B9u);
            for (int i = 0; i < count; i++)
            {
                float angle = i * (2f * math.PI / count);
                float radius = rng.NextFloat(
                    _settings.SpawnRingRadiusMin,
                    _settings.SpawnRingRadiusMax
                );
                var pos = new float3(
                    radius * math.cos(angle),
                    _settings.EnemySpawnY,
                    radius * math.sin(angle)
                );
                _world
                    .AddEntity<SampleTags.Enemy>()
                    .Set(new Position(pos))
                    .Set(new Armor { Value = _settings.EnemyArmors[i] })
                    .Set(new MaxHealth { Value = _settings.EnemyMaxHealth })
                    .Set(new Health { Value = _settings.EnemyMaxHealth })
                    .Set(new ChaseSpeed { Value = _settings.EnemyChaseSpeed })
                    .Set(new ColorComponent { Value = _settings.EnemyBaseColor });
            }
        }

        static GameObject CreateCube(float scale)
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Cube);
            go.transform.localScale = Vector3.one * scale;
            return go;
        }
    }
}
