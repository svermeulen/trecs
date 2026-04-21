// Companion docs: https://svermeulen.github.io/trecs/samples/13-save-game/

using System;
using System.Collections.Generic;
using TMPro;

namespace Trecs.Serialization.Samples.SaveGame
{
    public class SaveGameCompositionRoot : CompositionRootBase
    {
        public SaveGameSettings Settings = new();
        public TMP_Text HudText;

        SaveGameController _controller;
        SaveGameRenderer _renderer;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var world = new WorldBuilder()
                .SetSettings(
                    new WorldSettings
                    {
                        RandomSeed = Settings.RandomSeed,
                        RequireDeterministicSubmission = true,
                    }
                )
                .AddEntityTypes(
                    new[]
                    {
                        SaveGameTemplates.SaveGameGlobals.Template,
                        SaveGameTemplates.PlayerEntity.Template,
                        SaveGameTemplates.BoxEntity.Template,
                        SaveGameTemplates.TargetEntity.Template,
                        SaveGameTemplates.WallEntity.Template,
                    }
                )
                .Build();

            // Compose serialization. For a save-game-only sample the
            // RecordingHandler / PlaybackHandler are unused but harmless;
            // SerializationFactory.CreateAll wires everything up in one line.
            var serialization = SerializationFactory.CreateAll(world);

            _controller = new SaveGameController(serialization);
            _renderer = new SaveGameRenderer(world);
            var inputSystem = new PlayerInputSystem();
            var sceneInit = new SaveGameSceneInitializer(world);

            world.AddSystems(
                new ISystem[]
                {
                    inputSystem,
                    new PlayerMovementSystem(),
                    new TextDisplaySystem(HudText, _controller),
                }
            );

            initializables = new() { world.Initialize, sceneInit.Initialize };
            tickables = new() { _controller.Tick, inputSystem.Tick, world.Tick };
            lateTickables = new() { world.LateTick, _renderer.Tick };
            disposables = new()
            {
                _controller.Dispose,
                _renderer.Dispose,
                world.Dispose,
                serialization.Dispose,
            };
        }
    }
}
