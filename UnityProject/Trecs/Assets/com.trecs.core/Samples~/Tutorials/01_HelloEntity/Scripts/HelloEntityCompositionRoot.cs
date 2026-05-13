// Companion docs: https://svermeulen.github.io/trecs/samples/01-hello-entity/

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.HelloEntity
{
    public class HelloEntityCompositionRoot : CompositionRootBase
    {
        public float RotationSpeed = 2f;
        public Transform SpinnerCube;

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
                .AddTemplate(SampleTemplates.SpinnerEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new SpinnerSystem(RotationSpeed),
                    new SpinnerGameObjectUpdater(SpinnerCube),
                }
            );

            var sceneInitializer = new SceneInitializer(world);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
