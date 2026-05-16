// Companion docs: https://svermeulen.github.io/trecs/samples/19-multiple-worlds/

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    public class MultipleWorldsCompositionRoot : CompositionRootBase
    {
        public float SpawnIntervalA = 0.5f;
        public float SpawnIntervalB = 0.7f;
        public float Lifetime = 3f;
        public float SpawnRadius = 2f;
        public float WorldSeparation = 3f;

        World _worldA;
        World _worldB;
        bool _pausedA;
        bool _pausedB;

        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            // Each world gets its own RenderableGameObjectManager — the
            // manager's id counter lives in its world's heap so it must be
            // 1:1 with a World. GameObjectId values are world-local now,
            // not shared. Either world can be snapshotted independently
            // and its GameObjects will be rebuilt from its entity set.
            _worldA = new WorldBuilder()
                .SetDebugName("World A — Red Spheres")
                .AddTemplates(
                    new[]
                    {
                        SampleTemplates.SampleGlobals.Template,
                        SampleTemplates.CritterEntity.Template,
                    }
                )
                .Build();

            var goManagerA = new RenderableGameObjectManager(_worldA);

            _worldA.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(
                        SpawnIntervalA,
                        Lifetime,
                        SpawnRadius,
                        new Vector3(-WorldSeparation, 0, 0)
                    ),
                    new LifetimeSystem(),
                    new PrimitivePresenter(goManagerA),
                }
            );

            _worldB = new WorldBuilder()
                .SetDebugName("World B — Blue Cubes")
                .AddTemplates(
                    new[]
                    {
                        SampleTemplates.SampleGlobals.Template,
                        SampleTemplates.CritterEntity.Template,
                    }
                )
                .Build();

            var goManagerB = new RenderableGameObjectManager(_worldB);

            _worldB.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(
                        SpawnIntervalB,
                        Lifetime,
                        SpawnRadius,
                        new Vector3(WorldSeparation, 0, 0)
                    ),
                    new LifetimeSystem(),
                    new PrimitivePresenter(goManagerB),
                }
            );

            var sceneInitA = new SceneInitializer(goManagerA, PrimitiveType.Sphere, Color.red);
            var sceneInitB = new SceneInitializer(goManagerB, PrimitiveType.Cube, Color.blue);

            initializables = new()
            {
                _worldA.Initialize,
                _worldB.Initialize,
                sceneInitA.Initialize,
                sceneInitB.Initialize,
            };

            tickables = new()
            {
                ReadInput,
                () =>
                {
                    if (!_pausedA)
                        _worldA.Tick();
                },
                () =>
                {
                    if (!_pausedB)
                        _worldB.Tick();
                },
            };

            lateTickables = new()
            {
                () =>
                {
                    if (!_pausedA)
                        _worldA.LateTick();
                },
                () =>
                {
                    if (!_pausedB)
                        _worldB.LateTick();
                },
            };

            disposables = new()
            {
                goManagerA.Dispose,
                goManagerB.Dispose,
                _worldA.Dispose,
                _worldB.Dispose,
            };
        }

        void ReadInput()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1))
                _pausedA = !_pausedA;
            if (Input.GetKeyDown(KeyCode.Alpha2))
                _pausedB = !_pausedB;
        }

        void OnGUI()
        {
            if (_worldA == null)
                return;

            GUI.Label(
                new Rect(10, 10, 400, 20),
                $"World A: {(_pausedA ? "PAUSED" : "running")}  (press 1 to toggle)"
            );
            GUI.Label(
                new Rect(10, 30, 400, 20),
                $"World B: {(_pausedB ? "PAUSED" : "running")}  (press 2 to toggle)"
            );
            GUI.Label(
                new Rect(10, 60, 400, 20),
                $"WorldRegistry.ActiveWorlds.Count = {WorldRegistry.ActiveWorlds.Count}"
            );
        }
    }
}
