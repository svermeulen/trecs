// Companion docs: https://svermeulen.github.io/trecs/samples/11-snake/

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Trecs.Samples.Snake
{
    public class SnakeCompositionRoot : CompositionRootBase
    {
        public SnakeSettings Settings = new();
        public TMP_Text HudText;

        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            Application.targetFrameRate = 2000;

            var world = new WorldBuilder()
                .SetSettings(
                    new WorldSettings
                    {
                        RandomSeed = Settings.RandomSeed,
                        RequireDeterministicSubmission = true,
                    }
                )
                .AddTemplates(
                    new[]
                    {
                        SnakeTemplates.SnakeGlobals.Template,
                        SnakeTemplates.SnakeHeadEntity.Template,
                        SnakeTemplates.SnakeSegmentEntity.Template,
                        SnakeTemplates.SnakeFoodEntity.Template,
                    }
                )
                .Build();

            var goManager = new RenderableGameObjectManager(world);

            var sceneInit = new SnakeSceneInitializer(Settings, world, goManager);
            var inputSystem = new SnakeInputSystem();

            world.AddSystems(
                new ISystem[]
                {
                    inputSystem,
                    new SnakeMovementSystem(Settings),
                    new FoodConsumeSystem(),
                    new SegmentTrimSystem(),
                    new FoodSpawnSystem(Settings),
                    new SnakePresenter(goManager),
                    new TextDisplaySystem(HudText),
                }
            );

            initializables = new() { world.Initialize, sceneInit.Initialize };

            tickables = new() { inputSystem.Tick, world.Tick };

            lateTickables = new() { world.LateTick };

            disposables = new() { goManager.Dispose, world.Dispose };
        }
    }
}
