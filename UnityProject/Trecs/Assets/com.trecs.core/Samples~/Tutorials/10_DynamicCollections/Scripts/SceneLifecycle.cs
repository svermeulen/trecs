using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.DynamicCollections
{
    public partial class SceneLifecycle
    {
        readonly WorldAccessor _world;
        readonly SampleSettings _settings;
        readonly RenderableGameObjectManager _goManager;
        readonly DisposeCollection _subscriptions = new();

        public SceneLifecycle(
            World world,
            SampleSettings settings,
            RenderableGameObjectManager goManager
        )
        {
            _world = world.CreateAccessor(AccessorRole.Unrestricted);
            _settings = settings;
            _goManager = goManager;

            // Only the heap-backed trail variants need an on-remove cleanup
            // hook to free their backing storage. FixedArray and FixedList
            // live inline on the component and are freed automatically when
            // the entity is removed.
            switch (_settings.CollectionType)
            {
                case TrailCollectionType.UniquePtrQueue:
                    _world
                        .Events.EntitiesWithTags<DynamicCollectionsTags.QueueTrail>()
                        .OnRemoved(OnQueueRemoved)
                        .AddTo(_subscriptions);
                    break;
                case TrailCollectionType.TrecsListAppend:
                    _world
                        .Events.EntitiesWithTags<DynamicCollectionsTags.TrecsListTrail>()
                        .OnRemoved(OnTrecsListRemoved)
                        .AddTo(_subscriptions);
                    break;
                case TrailCollectionType.TrecsArrayRingBuffer:
                    _world
                        .Events.EntitiesWithTags<DynamicCollectionsTags.TrecsArrayTrail>()
                        .OnRemoved(OnTrecsArrayRemoved)
                        .AddTo(_subscriptions);
                    break;
            }

            _world.Events.OnShutdown(() => _subscriptions.Dispose()).AddTo(_subscriptions);
        }

        [ForEachEntity]
        void OnQueueRemoved(in TrailQueue trail)
        {
            trail.Value.Dispose(_world);
        }

        [ForEachEntity]
        void OnTrecsListRemoved(in TrailTrecsList trail)
        {
            trail.Value.Dispose(_world);
        }

        [ForEachEntity]
        void OnTrecsArrayRemoved(in TrailTrecsArray trail)
        {
            trail.Positions.Dispose(_world);
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(DynamicCollectionsPrefabs.Character, CreateCharacter);

            for (int i = 0; i < _settings.CharacterCount; i++)
            {
                float offset = _world.FixedRng.NextFloat(0f, 1000f);
                var initialPosition = CharacterMover.WanderAt(
                    offset,
                    0f,
                    _settings.WanderExtent,
                    _settings.WanderTimeScale
                );

                switch (_settings.CollectionType)
                {
                    case TrailCollectionType.UniquePtrQueue:
                        SpawnQueue(offset, initialPosition);
                        break;
                    case TrailCollectionType.FixedArrayRingBuffer:
                        SpawnFixedArray(offset, initialPosition);
                        break;
                    case TrailCollectionType.FixedListAppend:
                        SpawnFixedList(offset, initialPosition);
                        break;
                    case TrailCollectionType.TrecsListAppend:
                        SpawnTrecsList(offset, initialPosition);
                        break;
                    case TrailCollectionType.TrecsArrayRingBuffer:
                        SpawnTrecsArray(offset, initialPosition);
                        break;
                }
            }
        }

        void SpawnQueue(float offset, float3 initialPosition)
        {
            // ─── Allocate UniquePtr ─────────────────────────────────
            // Each entity gets its own empty Queue that will grow as the
            // entity moves. UniquePtr.Alloc stores the managed object in
            // the world's UniqueHeap and returns a 4-byte handle that we
            // embed in the entity's TrailQueue component.
            var trailPtr = UniquePtr.Alloc(_world, new Queue<float3>());

            _world
                .AddEntity<DynamicCollectionsTags.Character, DynamicCollectionsTags.QueueTrail>()
                .Set(new Position(initialPosition))
                .Set(new NoiseOffset(offset))
                .Set(new LastSamplePosition(initialPosition))
                .Set(new TrailQueue(trailPtr));
        }

        void SpawnFixedArray(float offset, float3 initialPosition)
        {
            // Default-zeroed Trail is an empty ring buffer (Head = Count = 0);
            // the FixedArray32 storage is inline so no heap allocation needed.
            _world
                .AddEntity<
                    DynamicCollectionsTags.Character,
                    DynamicCollectionsTags.FixedArrayTrail
                >()
                .Set(new Position(initialPosition))
                .Set(new NoiseOffset(offset))
                .Set(new LastSamplePosition(initialPosition))
                .Set(default(TrailFixedArray));
        }

        void SpawnFixedList(float offset, float3 initialPosition)
        {
            // Default-zeroed Trail is an empty ring buffer
            // (Head = 0, Positions.Count = 0); the FixedList256 storage is
            // inline so no heap allocation needed.
            _world
                .AddEntity<
                    DynamicCollectionsTags.Character,
                    DynamicCollectionsTags.FixedListTrail
                >()
                .Set(new Position(initialPosition))
                .Set(new NoiseOffset(offset))
                .Set(new LastSamplePosition(initialPosition))
                .Set(default(TrailFixedList));
        }

        void SpawnTrecsList(float offset, float3 initialPosition)
        {
            // TrecsList.Alloc reserves a header + initial-capacity slot on
            // the world's shared native chunk store and returns a 4-byte handle.
            // The updater grows it geometrically via EnsureCapacity.
            var listHandle = TrecsList.Alloc<float3>(_world, initialCapacity: 16);

            _world
                .AddEntity<
                    DynamicCollectionsTags.Character,
                    DynamicCollectionsTags.TrecsListTrail
                >()
                .Set(new Position(initialPosition))
                .Set(new NoiseOffset(offset))
                .Set(new LastSamplePosition(initialPosition))
                .Set(new TrailTrecsList(listHandle));
        }

        void SpawnTrecsArray(float offset, float3 initialPosition)
        {
            // TrecsArray.Alloc reserves a single fixed-length data slot on the
            // world's shared native chunk store and returns an 8-byte handle
            // (4-byte PtrHandle + 4-byte Length inline). Length is fixed at
            // allocation time — we pick SampleSettings.TrailLength and use the
            // buffer as a ring buffer (Head/Count, same as FixedArrayRingBuffer).
            var arrayHandle = TrecsArray.Alloc<float3>(_world, length: _settings.TrailLength);

            _world
                .AddEntity<
                    DynamicCollectionsTags.Character,
                    DynamicCollectionsTags.TrecsArrayTrail
                >()
                .Set(new Position(initialPosition))
                .Set(new NoiseOffset(offset))
                .Set(new LastSamplePosition(initialPosition))
                .Set(
                    new TrailTrecsArray
                    {
                        Positions = arrayHandle,
                        Head = 0,
                        Count = 0,
                    }
                );
        }

        static GameObject CreateCharacter()
        {
            var color = Color.cyan;

            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * 0.5f;
            go.GetComponent<Renderer>().material.color = color;

            // Add LineRenderer for trail visualization. Trails are written
            // oldest-first (index 0 = oldest, last index = character), so the
            // LineRenderer's "end" sits at the character — make that the wide,
            // opaque end and let the trailing tail fade.
            var lr = go.AddComponent<LineRenderer>();
            lr.startWidth = 0.02f;
            lr.endWidth = 0.15f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = new Color(color.r, color.g, color.b, 0.3f);
            lr.endColor = color;
            lr.positionCount = 0;

            return go;
        }
    }
}
