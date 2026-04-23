using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.AspectInterfaces
{
    public class SceneInitializer
    {
        static readonly Color EnemyBaseColor = Color.red;
        static readonly Color BossBaseColor = new Color(0.6f, 0.2f, 0.8f);

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
            // One boss, stationary at the zone center — takes damage forever,
            // self-heals when enraged, visibly oscillates at ~50% HP.
            var bossPos = _settings.ZoneCenter;
            var bossGO = MakePrimitive(PrimitiveType.Cube, BossBaseColor, "Boss", bossPos);
            bossGO.transform.localScale = Vector3.one * 1.2f;
            _world
                .AddEntity<SampleTags.Boss>()
                .Set(new Position(bossPos))
                .Set(new ColorComponent { Value = BossBaseColor })
                .Set(_gameObjectRegistry.Register(bossGO));

            // Six enemies on a ring around the boss. Four start inside the
            // zone (radius ZoneRadius = 5) and two start just outside. Each
            // enemy's per-frame AI decides whether to charge, hold, or flee.
            int count = 6;
            float ringRadius = _settings.ZoneRadius * 0.8f;
            for (int i = 0; i < count; i++)
            {
                float angle = i * (2f * math.PI / count);
                float r = (i < 4) ? ringRadius : _settings.ZoneRadius * 1.3f;
                var pos = new float3(r * math.cos(angle), 0.3f, r * math.sin(angle));
                var go = MakePrimitive(PrimitiveType.Cube, EnemyBaseColor, $"Enemy_{i}", pos);
                _world
                    .AddEntity<SampleTags.Enemy>()
                    .Set(new Position(pos))
                    .Set(new ColorComponent { Value = EnemyBaseColor })
                    .Set(_gameObjectRegistry.Register(go));
            }

            // Visual marker for the damage zone (flat orange disc on the ground).
            var marker = SampleUtil.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = "DamageZone";
            marker.transform.position = (Vector3)_settings.ZoneCenter;
            marker.transform.localScale = new Vector3(
                _settings.ZoneRadius * 2f,
                0.05f,
                _settings.ZoneRadius * 2f
            );
            marker.GetComponent<Renderer>().material.color = new Color(1f, 0.6f, 0f, 1f);
        }

        static GameObject MakePrimitive(PrimitiveType type, Color color, string name, float3 pos)
        {
            var go = SampleUtil.CreatePrimitive(type);
            go.name = name;
            go.transform.position = (Vector3)pos;
            go.transform.localScale = Vector3.one * 0.6f;
            go.GetComponent<Renderer>().material.color = color;
            return go;
        }
    }
}
