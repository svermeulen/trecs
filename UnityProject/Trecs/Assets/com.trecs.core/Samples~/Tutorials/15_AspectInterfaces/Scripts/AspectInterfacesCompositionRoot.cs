// Companion docs: https://svermeulen.github.io/trecs/samples/15-aspect-interfaces/

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Trecs.Samples.AspectInterfaces
{
    public class AspectInterfacesCompositionRoot : CompositionRootBase
    {
        public SampleSettings Settings;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.EnemyEntity.Template)
                .AddEntityType(SampleTemplates.BossEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new EnemyAiSystem(
                        Settings.ZoneCenter,
                        Settings.ZoneRadius,
                        Settings.DamagePerHit,
                        Settings.HitInterval,
                        Settings.EnemyOutOfZoneRegenPerSecond
                    ),
                    new BossAiSystem(
                        Settings.ZoneCenter,
                        Settings.ZoneRadius,
                        Settings.DamagePerHit,
                        Settings.HitInterval,
                        Settings.BossEnragedRegenPerSecond
                    ),
                    new HitFlashRenderer(gameObjectRegistry),
                }
            );

            var sceneInitializer = new SceneInitializer(world, gameObjectRegistry, Settings);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }

    [Serializable]
    public class SampleSettings
    {
        public float3 ZoneCenter = new(0f, 0f, 0f);
        public float ZoneRadius = 5f;

        // Damage is applied in discrete pulses rather than continuously, so
        // the hit flash has a visible off-period between hits and the base
        // color shows through. Each pulse deals DamagePerHit raw damage
        // (reduced by each entity's Armor).
        public float DamagePerHit = 30f;
        public float HitInterval = 0.5f;

        // Boss self-heal rate (per second) once EnrageLevel > 0.5. Tuned so
        // the boss stabilizes near the enrage threshold rather than dying.
        public float BossEnragedRegenPerSecond = 25f;

        // Enemy passive-heal rate (per second) when safely outside the zone.
        // Without this, fled enemies would drift away forever; with it, the
        // scene becomes cyclic — creep in, get hit, flee out, heal, creep in.
        public float EnemyOutOfZoneRegenPerSecond = 10f;
    }
}
