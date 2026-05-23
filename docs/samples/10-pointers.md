# 10 — Pointers

Heap pointers for managed data (classes, lists, arrays) that can't live in unmanaged components.

**Source:** `com.trecs.core/Samples~/Tutorials/10_Pointers/`

## What it does

A handful of follower entities trace a figure-8 path, each leaving a fading trail behind it (rendered with a Unity `LineRenderer`). Per-entity trail history is a `Queue<Vector3>` — managed data that can't live in a component — held on the heap behind a `UniquePtr<Queue<Vector3>>`.

## Schema

### Components

```csharp
// 4-byte handle, stored inline in the component
public partial struct Trail : IEntityComponent
{
    public UniquePtr<Queue<Vector3>> Value;
}

[Unwrap]
public partial struct PathPhase : IEntityComponent
{
    public float Value;  // current position along the figure-8, in radians
}
```

### Template

```csharp
public partial class PatrolFollowerEntity
    : ITemplate,
        IExtends<CommonTemplates.RenderableGameObject>,
        ITagged<PatrolTags.Follower>
{
    Position Position;
    PathPhase PathPhase;
    Trail Trail;
    PrefabId PrefabId = new(PointersPrefabs.Follower);
}
```

## Allocation

The scene initializer creates each follower and allocates its own empty `Queue<Vector3>` blob. `UniquePtr.Alloc` returns a 4-byte handle that goes straight onto the component:

```csharp
for (int i = 0; i < _followerCount; i++)
{
    float phase = (float)i / _followerCount * 2f * math.PI;
    var trailPtr = UniquePtr.Alloc(_world.Heap, new Queue<Vector3>());

    _world.AddEntity<PatrolTags.Follower>()
        .Set(new Position(PatrolMovementSystem.FigureEightAt(phase)))
        .Set(new PathPhase(phase))
        .Set(new Trail { Value = trailPtr });
}
```

Each entity owns its own trail, so `UniquePtr<T>` is the right type — no refcounting, single owner.

## Movement system

The system advances each follower along the figure-8 and appends to its trail. `trail.Value.Get(World)` returns the live `Queue<Vector3>` instance — mutating it sticks because we're holding the object reference. We `Enqueue` the new position and `Dequeue` the oldest once the trail hits its cap, both O(1) on `Queue<T>`:

```csharp
public partial class PatrolMovementSystem : ISystem
{
    const float Speed = 1.5f;
    const int TrailLength = 30;

    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Position position, ref PathPhase phase, in Trail trail)
    {
        phase.Value = (phase.Value + Speed * World.DeltaTime) % (2f * math.PI);
        position.Value = FigureEightAt(phase.Value);

        var trailQueue = trail.Value.Get(World);
        trailQueue.Enqueue((Vector3)position.Value);

        while (trailQueue.Count > TrailLength)
            trailQueue.Dequeue();
    }
}
```

Note `in Trail trail` is enough — we're not replacing the pointer, just dereferencing it to mutate the managed object behind it.

## Cleanup

Pointers stored on components must be disposed when the entity is removed — Trecs does **not** auto-dispose. The standard pattern is an `OnRemoved` observer with a `[ForEachEntity]` handler that receives the component to dispose:

```csharp
public partial class PatrolFollowerCleanup : IDisposable
{
    readonly DisposeCollection _disposables = new();

    public PatrolFollowerCleanup(World world)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);

        World.Events.EntitiesWithTags<PatrolTags.Follower>()
            .OnRemoved(OnFollowerRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnFollowerRemoved(in Trail trail)
    {
        trail.Value.Dispose(World);
    }

    public void Dispose() => _disposables.Dispose();
}
```

In DEBUG builds Trecs reports any pointers still alive at world shutdown — handy for catching missed cleanup paths.

## Snapshot round-trip

`Queue<Vector3>` is a managed collection, so Trecs's blit serializer can't round-trip it through the Trecs Player. Trecs core ships a generic `QueueSerializer<T>` — the sample just registers the closed `QueueSerializer<Vector3>` against `world.SerializerRegistry` at composition time and the snapshot writer / reader handles the rest. See [Serialization](../experimental/serialization.md) for the broader pattern and for authoring your own `ISerializer<T>` when the payload isn't a built-in collection.

## Concepts introduced

- **`UniquePtr<T>`** — single-owner managed pointer. Stored inline as a 4-byte handle on the component. See [Pointers](../experimental/pointers.md).
- **`UniquePtr.Alloc(World.Heap, value)`** — static factory; mirrors `UniquePtr.Alloc(World, value)` if you have the `WorldAccessor` handy.
- **`ptr.Get(World)`** — dereferences to the managed object reference.
- **`ptr.Dispose(World)`** — returns the slot to the pool.
- **`OnRemoved` cleanup observer** — the canonical way to release pointers when entities disappear. See [Pointers — cleanup is manual](../experimental/pointers.md#cleanup-is-manual-for-entity-owned-pointers) and [Entity Events](../entity-management/entity-events.md).
- For sharing the same managed data across many entities, see `SharedPtr<T>` in [Sample 15 — Blob Seed Pattern](15-blob-seed-pattern.md).
- For Burst-compatible variants used inside jobs, see [Pointers in jobs](../experimental/pointers.md#pointers-in-jobs).
