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

        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var world = new WorldBuilder()
                .AddTemplate(SampleTemplates.ParticleEntity.Template)
                .AddSet<SampleSets.WaveX>()
                .AddSet<SampleSets.WaveZ>()
                .Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[]
                {
                    new WaveMembershipSystem(Settings),
                    new WaveXEffectSystem(Settings),
                    new WaveZEffectSystem(Settings),
                    new ParticlePresenter(Settings, goManager),
                }
            );

            var sceneInitializer = new SceneInitializer(world, Settings, goManager);

            initializables = new()
            {
                world.Initialize,
                sceneInitializer.Initialize,
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { goManager.Dispose, world.Dispose };
        }
    }
}
