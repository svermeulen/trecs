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

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            // Both worlds share a single GameObjectRegistry because GameObjectId is just a process-wide
            // integer handle into Unity's scene — the registry has nothing to do with ECS isolation.
            // Each world still has its own entity store, system instances, and accessors.
            var gameObjectRegistry = new GameObjectRegistry();

            _worldA = new WorldBuilder()
                .SetDebugName("World A — Red Spheres")
                .AddEntityType(SampleTemplates.CritterEntity.Template)
                .Build();

            _worldA.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(
                        SpawnIntervalA,
                        Lifetime,
                        SpawnRadius,
                        new Vector3(-WorldSeparation, 0, 0),
                        Color.red,
                        PrimitiveType.Sphere,
                        gameObjectRegistry
                    ),
                    new LifetimeSystem(gameObjectRegistry),
                    new PrimitiveRendererSystem(gameObjectRegistry),
                }
            );

            _worldB = new WorldBuilder()
                .SetDebugName("World B — Blue Cubes")
                .AddEntityType(SampleTemplates.CritterEntity.Template)
                .Build();

            _worldB.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(
                        SpawnIntervalB,
                        Lifetime,
                        SpawnRadius,
                        new Vector3(WorldSeparation, 0, 0),
                        Color.blue,
                        PrimitiveType.Cube,
                        gameObjectRegistry
                    ),
                    new LifetimeSystem(gameObjectRegistry),
                    new PrimitiveRendererSystem(gameObjectRegistry),
                }
            );

            initializables = new() { _worldA.Initialize, _worldB.Initialize };

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

            disposables = new() { _worldA.Dispose, _worldB.Dispose };
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
