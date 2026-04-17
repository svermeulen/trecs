// Companion docs: https://svermeulen.github.io/trecs/samples/08-sets/
using System;
using System.Collections.Generic;

namespace Trecs.Samples.Sets
{
    /// <summary>
    /// Demonstrates overlapping entity sets: two sine waves sweep across
    /// a grid along perpendicular axes. Each wave has its own set.
    /// Effect systems iterate only their wave's subset, and particles
    /// in both waves receive combined effects — something that would
    /// require 2^N template states but needs only N sets.
    /// </summary>
    public class SetsCompositionRoot : CompositionRootBase
    {
        public SampleSettings Settings = new();

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.ParticleEntity.Template)
                .AddSet<SampleSets.WaveX>()
                .AddSet<SampleSets.WaveZ>()
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new WaveMembershipSystem(Settings),
                    new WaveXEffectSystem(Settings),
                    new WaveZEffectSystem(Settings),
                    new ParticleRendererSystem(Settings, gameObjectRegistry),
                }
            );

            var sceneInitializer = new SceneInitializer(world, gameObjectRegistry, Settings);

            initializables = new()
            {
                world.Initialize,
                sceneInitializer.Initialize,
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
