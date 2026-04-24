using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Trecs.Samples.AspectInterfaces
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly SampleSettings _settings;

        public SceneInitializer(
            World world,
            GameObjectRegistry gameObjectRegistry,
            SampleSettings settings
        )
        {
            _world = world.CreateAccessor();
            _gameObjectRegistry = gameObjectRegistry;
            _settings = settings;
        }

        public void Initialize()
        {
            // One boss at the origin. Every per-entity stat is pulled
            // from SampleSettings so the scene inspector is the single
            // source of truth.
            var bossPos = float3.zero;
            var bossGO = MakePrimitive(
                PrimitiveType.Cube,
                _settings.BossBaseColor,
                "Boss",
                bossPos,
                _settings.BossScale
            );
            _world
                .AddEntity<SampleTags.Boss>()
                .Set(new Position(bossPos))
                .Set(new Armor { Value = _settings.BossArmor })
                .Set(new MaxHealth { Value = _settings.BossMaxHealth })
                .Set(new Health { Value = _settings.BossMaxHealth })
                .Set(new ColorComponent { Value = _settings.BossBaseColor })
                .Set(_gameObjectRegistry.Register(bossGO));

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
                var go = MakePrimitive(
                    PrimitiveType.Cube,
                    _settings.EnemyBaseColor,
                    $"Enemy_{i}",
                    pos,
                    _settings.EnemyScale
                );
                _world
                    .AddEntity<SampleTags.Enemy>()
                    .Set(new Position(pos))
                    .Set(new Armor { Value = _settings.EnemyArmors[i] })
                    .Set(new MaxHealth { Value = _settings.EnemyMaxHealth })
                    .Set(new Health { Value = _settings.EnemyMaxHealth })
                    .Set(new ChaseSpeed { Value = _settings.EnemyChaseSpeed })
                    .Set(new ColorComponent { Value = _settings.EnemyBaseColor })
                    .Set(_gameObjectRegistry.Register(go));
            }
        }

        static GameObject MakePrimitive(
            PrimitiveType type,
            Color color,
            string name,
            float3 pos,
            float scale
        )
        {
            var go = SampleUtil.CreatePrimitive(type);
            go.name = name;
            go.transform.position = (Vector3)pos;
            go.transform.localScale = Vector3.one * scale;
            go.GetComponent<Renderer>().material.color = color;
            return go;
        }
    }
}
