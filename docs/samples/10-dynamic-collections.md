# 10 — Dynamic Collections

Five ways to attach a dynamic per-entity collection to a Trecs component, compared side by side.

**Source:** `com.trecs.core/Samples~/Tutorials/10_DynamicCollections/`

## What it does

A handful of characters wander around a box on the XZ plane driven by 2D Perlin noise, each leaving a fading trail rendered with a Unity `LineRenderer`. The trail backing is selected at composition time via an inspector enum — pick one of the five variants below and the composition root spawns the matching template variant and trail systems.

## The five trail variants

| Variant | Storage | Inline? | Growable? | Cleanup needed? |
|---------|---------|---------|-----------|-----------------|
| **UniquePtr Queue** | Managed `Queue<float3>` behind a `UniquePtr<T>` | No (4-byte handle) | Yes | Yes (`OnRemoved`) |
| **FixedArray ring buffer** | `FixedArray32<float3>` with `Head`/`Count` | Yes (blittable) | No (fixed capacity) | No |
| **FixedList append** | `FixedList32<float3>` with `Head` | Yes (blittable) | No (appends until full) | No |
| **TrecsList append** | `TrecsList<float3>` on the shared native chunk store | No (4-byte handle) | Yes (geometric) | Yes (`OnRemoved`) |
| **TrecsArray ring buffer** | `TrecsArray<float3>` on the shared native chunk store | No (8-byte handle) | No (length fixed at alloc) | Yes (`OnRemoved`) |

## Schema

Each variant extends a shared `Character` base template and adds its own trail component plus a distinguishing tag:

```csharp
public partial class Character
    : ITemplate,
        IExtends<CommonTemplates.RenderableGameObject>,
        ITagged<DynamicCollectionsTags.Character>
{
    Position Position;
    NoiseOffset NoiseOffset;
    LastSamplePosition LastSamplePosition;
    PrefabId PrefabId = new(DynamicCollectionsPrefabs.Character);
}

// One variant — the other four follow the same pattern
public partial class CharacterQueue
    : ITemplate,
        IExtends<Character>,
        ITagged<DynamicCollectionsTags.QueueTrail>
{
    TrailQueue TrailQueue;
}
```

### Trail components (one per variant)

```csharp
// Managed Queue behind a UniquePtr — 4-byte handle on the component
[Unwrap]
public partial struct TrailQueue : IEntityComponent
{
    public UniquePtr<Queue<float3>> Value;
}

// Inline FixedArray32 ring buffer — blittable, no heap allocation
public partial struct TrailFixedArray : IEntityComponent
{
    public FixedArray32<float3> Positions;
    public int Head;
    public int Count;
}

// Heap-backed TrecsList — growable, 4-byte handle
[Unwrap]
public partial struct TrailTrecsList : IEntityComponent
{
    public TrecsList<float3> Value;
}
```

## Spawning

The scene initializer picks the right spawn path per variant. The UniquePtr and Trecs collection variants allocate their backing storage before setting it on the entity:

```csharp
// UniquePtr<Queue<float3>> — allocate a managed Queue on the world's UniqueHeap
var trailPtr = UniquePtr.Alloc(_world, new Queue<float3>());

_world.AddEntity<DynamicCollectionsTags.Character, DynamicCollectionsTags.QueueTrail>()
    .Set(new Position(initialPosition))
    .Set(new NoiseOffset(offset))
    .Set(new LastSamplePosition(initialPosition))
    .Set(new TrailQueue(trailPtr));

// TrecsList — allocate on the shared native chunk store
var listHandle = TrecsList.Alloc<float3>(_world, initialCapacity: 16);

_world.AddEntity<DynamicCollectionsTags.Character, DynamicCollectionsTags.TrecsListTrail>()
    .Set(new Position(initialPosition))
    .Set(new NoiseOffset(offset))
    .Set(new LastSamplePosition(initialPosition))
    .Set(new TrailTrecsList(listHandle));
```

The inline variants (FixedArray, FixedList) need no heap allocation — `default` is a valid empty state.

## Trail updater (Queue variant)

Each variant has its own updater system. The Queue variant reads through the `UniquePtr` to get the live `Queue<float3>` and uses it as a ring buffer trimmed to the configured trail length:

```csharp
[ExecuteAfter(typeof(CharacterMover))]
public partial class QueueTrailUpdater : ISystem
{
    readonly SampleSettings _settings;

    [ForEachEntity(
        typeof(DynamicCollectionsTags.Character),
        typeof(DynamicCollectionsTags.QueueTrail)
    )]
    void Execute(in Character character)
    {
        if (math.distance(character.Position, character.LastSamplePosition)
            < _settings.TrailMinSampleDistance)
            return;

        var queue = character.TrailQueue.Get(World);
        queue.Enqueue(character.Position);

        while (queue.Count > _settings.TrailLength)
            queue.Dequeue();

        character.LastSamplePosition = character.Position;
    }

    partial struct Character
        : IAspect,
            IRead<Position, TrailQueue>,
            IWrite<LastSamplePosition> { }
}
```

## Cleanup

Heap-backed variants (UniquePtr Queue, TrecsList, TrecsArray) must be disposed when the entity is removed — Trecs does **not** auto-dispose. The inline variants (FixedArray, FixedList) need no cleanup. The scene lifecycle registers `OnRemoved` observers for the heap-backed variants:

```csharp
_world.Events.EntitiesWithTags<DynamicCollectionsTags.QueueTrail>()
    .OnRemoved(OnQueueRemoved)
    .AddTo(_disposables);

[ForEachEntity]
void OnQueueRemoved(in TrailQueue trail)
{
    trail.Value.Dispose(_world);
}
```

## Concepts introduced

- **`UniquePtr<T>`** — single-owner managed pointer. See [Pointers](../experimental/pointers.md).
- **`FixedArray32<T>`** / **`FixedList32<T>`** — inline blittable collections with fixed capacity. See [Fixed Collections](../advanced/fixed-collections.md).
- **`TrecsList<T>`** / **`TrecsArray<T>`** — heap-backed native collections on the world's shared chunk store. See [Dynamic Collections](../experimental/dynamic-collections.md).
- **Per-variant template inheritance** — `IExtends<Character>` plus a per-variant tag and trail component.
- **`OnRemoved` cleanup observer** — the canonical way to release heap-backed data when entities disappear. See [Entity Events](../entity-management/entity-events.md).
