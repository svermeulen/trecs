using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Demonstrates heap pointers: SharedPtr and UniquePtr.
    ///
    /// Two teams of cubes orbit side-by-side:
    /// - LEFT (cyan): Team A — all members share one TeamConfig via SharedPtr
    /// - RIGHT (magenta): Team B — all members share a different TeamConfig
    ///
    /// Each cube also has its own UniquePtr&lt;EntityState&gt; that tracks a
    /// per-entity frame counter. The counter drives a scale pulse, so each
    /// cube pulses at a different phase — demonstrating unique-per-entity state.
    ///
    /// Key concepts:
    /// 1. SharedPtr — reference-counted shared objects (Clone to distribute)
    /// 2. UniquePtr — exclusive mutable per-entity objects
    /// 3. Pointer disposal — OnRemoved event cleans up when entities are destroyed
    /// </summary>
    public class PointersCompositionRoot : CompositionRootBase
    {
        public int EntitiesPerTeam = 6;

        DisposeCollection _eventDisposables;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();
            _eventDisposables = new DisposeCollection();

            // SharedPtr/UniquePtr require a writable blob store. The in-memory
            // store is sufficient for samples — no on-disk persistence needed.
            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                new BlobStoreCommon(null)
            );

            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddTemplate(SampleTemplates.TeamMemberEntity.Template)
                .AddBlobStore(blobStore)
                .Build();

            world.AddSystems(
                new ISystem[] { new TeamOrbitSystem(), new TeamRendererSystem(gameObjectRegistry) }
            );

            // Register cleanup handler BEFORE spawning, so it catches
            // all future removals (including world disposal).
            RegisterPointerCleanup(world);

            initializables = new()
            {
                world.Initialize,
                () => SpawnTeams(world, gameObjectRegistry),
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { _eventDisposables.Dispose, world.Dispose };
        }

        /// <summary>
        /// Pointers stored in components MUST be disposed manually when
        /// entities are removed. Subscribe to OnRemoved to handle this.
        /// Without cleanup, disposed pointers leak and generate warnings.
        /// </summary>
        void RegisterPointerCleanup(World world)
        {
            var cleanupAccessor = world.CreateAccessor();

            cleanupAccessor
                .Events.InGroupsWithTags<TeamTags.Member>()
                .OnRemoved(
                    (Group group, EntityRange indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                        {
                            var entityIndex = new EntityIndex(i, group);
                            var member = cleanupAccessor.Component<TeamMember>(entityIndex).Read;

                            // Dispose SharedPtr: decrements refcount.
                            // Object is freed when last clone is disposed.
                            member.Config.Dispose(cleanupAccessor);

                            // Dispose UniquePtr: returns object to pool.
                            member.State.Dispose(cleanupAccessor);
                        }
                    }
                )
                .AddTo(_eventDisposables);
        }

        void SpawnTeams(World world, GameObjectRegistry gameObjectRegistry)
        {
            var ecs = world.CreateAccessor();

            // ─── Allocate shared team configs ────────────────────────
            // AllocShared creates a heap object and returns a SharedPtr.
            // The object is reference-counted: starts at refcount 1.
            var teamAConfig = ecs.Heap.AllocShared(
                new TeamConfig
                {
                    Color = Color.cyan,
                    OrbitSpeed = 1.5f,
                    OrbitRadius = 3f,
                    CenterX = -5f,
                }
            );

            var teamBConfig = ecs.Heap.AllocShared(
                new TeamConfig
                {
                    Color = Color.magenta,
                    OrbitSpeed = 2.5f,
                    OrbitRadius = 2f,
                    CenterX = 5f,
                }
            );

            // ─── Spawn team members ─────────────────────────────────
            SpawnTeam(ecs, gameObjectRegistry, teamAConfig, EntitiesPerTeam);
            SpawnTeam(ecs, gameObjectRegistry, teamBConfig, EntitiesPerTeam);

            // ─── Dispose original SharedPtrs ────────────────────────
            // Each entity now holds its own Clone. The original ptrs are
            // no longer needed — dispose them to decrement the refcount.
            // The object stays alive because clones still reference it.
            teamAConfig.Dispose(ecs);
            teamBConfig.Dispose(ecs);
        }

        void SpawnTeam(
            WorldAccessor ecs,
            GameObjectRegistry gameObjectRegistry,
            SharedPtr<TeamConfig> configPtr,
            int count
        )
        {
            var config = configPtr.Get(ecs);

            for (int i = 0; i < count; i++)
            {
                float phase = (float)i / count * 2f * math.PI;

                var position = new float3(
                    config.CenterX + math.cos(phase) * config.OrbitRadius,
                    0.5f,
                    math.sin(phase) * config.OrbitRadius
                );

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Team_{config.Color}_{i}";
                go.transform.position = (Vector3)position;
                go.transform.localScale = Vector3.one * 0.6f;
                go.GetComponent<Renderer>().material.color = config.Color;

                // ─── Clone SharedPtr ────────────────────────────
                // Clone increments the refcount. Each entity gets its
                // own SharedPtr handle, all pointing to the same object.
                var configClone = configPtr.Clone(ecs);

                // ─── Allocate UniquePtr ─────────────────────────
                // AllocUnique creates a new, exclusive object per entity.
                // Stagger initial frame count for visual variety.
                var statePtr = ecs.Heap.AllocUnique(new EntityState { FrameCount = i * 10 });

                ecs.AddEntity<TeamTags.Member>()
                    .Set(new Position(position))
                    .Set(
                        new TeamMember
                        {
                            Config = configClone,
                            State = statePtr,
                            Phase = phase,
                        }
                    )
                    .Set(gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }
    }
}
