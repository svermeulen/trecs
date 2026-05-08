// Companion docs: https://svermeulen.github.io/trecs/samples/11-snake/

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Trecs.Serialization.Samples.Snake
{
    public class SnakeCompositionRoot : CompositionRootBase
    {
        public SnakeSettings Settings = new();
        public TMP_Text HudText;

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
            RegisterPrefabs(goManager);

            // SerializationFactory bundles the registry + WorldStateSerializer +
            // SnapshotSerializer + RecordingBundleSerializer + BundleRecorder +
            // BundlePlayer. All of Snake's components are blittable so no custom
            // serializers are needed; if you add a non-blittable component to
            // your own game, register a custom ISerializer<T> via
            // serialization.Registry here.
            var serialization = SerializationFactory.CreateAll(world);

            var recordController = new RecordAndPlaybackController(
                serialization,
                world,
                sampleName: "Snake"
            );
            var sceneInit = new SnakeSceneInitializer(Settings, world);
            var inputSystem = new SnakeInputSystem();

            world.AddSystems(
                new ISystem[]
                {
                    inputSystem,
                    new SnakeMovementSystem(Settings),
                    new FoodConsumeSystem(),
                    new SegmentTrimSystem(),
                    new FoodSpawnSystem(Settings),
                    new SnakeRendererSystem(goManager),
                    new TextDisplaySystem(HudText, recordController),
                }
            );

            initializables = new() { world.Initialize, sceneInit.Initialize };

            tickables = new() { recordController.Tick, inputSystem.Tick, world.Tick };

            lateTickables = new() { world.LateTick };

            disposables = new()
            {
                recordController.Dispose,
                goManager.Dispose,
                serialization.Dispose,
                world.Dispose,
            };
        }

        static void RegisterPrefabs(RenderableGameObjectManager goManager)
        {
            var headMat = SampleUtil.CreateMaterial(new Color(0.2f, 0.9f, 0.4f));
            var segmentMat = SampleUtil.CreateMaterial(new Color(0.4f, 0.7f, 0.3f));
            var foodMat = SampleUtil.CreateMaterial(new Color(0.9f, 0.7f, 0.2f));

            goManager.RegisterFactory(SnakePrefabs.Head, () => CreateCube(headMat, 1.0f, "Head"));
            goManager.RegisterFactory(
                SnakePrefabs.Segment,
                () => CreateCube(segmentMat, 0.85f, "Segment")
            );
            goManager.RegisterFactory(SnakePrefabs.Food, () => CreateCube(foodMat, 0.6f, "Food"));
        }

        static GameObject CreateCube(Material material, float scale, string name)
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.localScale = Vector3.one * scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            Destroy(go.GetComponent<Collider>());
            return go;
        }
    }
}
