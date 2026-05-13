// Companion docs: https://svermeulen.github.io/trecs/samples/06-partitions/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.Partitions
{
    /// <summary>
    /// Demonstrates template partitions: entities grouped by partition tag for
    /// cache-friendly iteration.
    ///
    /// Balls bounce around with physics (Active partition) then come to rest
    /// on the ground (Resting partition). After a timer, resting balls launch
    /// back up. The physics system only touches Active balls — which are
    /// stored contiguously in memory, giving optimal cache performance.
    /// </summary>
    public class PartitionsCompositionRoot : CompositionRootBase
    {
        public int BallCount = 100;
        public float SpawnRadius = 8f;

        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var world = new WorldBuilder().AddTemplate(SampleTemplates.BallEntity.Template).Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[]
                {
                    new PhysicsSystem(),
                    new WakeUpSystem(),
                    new BallPresenter(goManager),
                }
            );

            var sceneInitializer = new SceneInitializer(world, BallCount, SpawnRadius, goManager);

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
