# 10 — Pointers

Heap pointers for managed data (classes, lists, arrays) that can't live in unmanaged components.

**Source:** `com.trecs.core/Samples~/Tutorials/10_Pointers/`

## What it does

A handful of follower entities trace a figure-8 path, each leaving a fading trail behind it (rendered with a Unity `LineRenderer`). Per-entity trail history is a `List<Vector3>` — managed data that can't live in a component — held on the heap behind a `UniquePtr<TrailHistory>`.

## Schema

### Managed payload

```csharp
public class TrailHistory
{
    public List<Vector3> Positions = new();
    public int MaxLength;
}
```

`TrailHistory` is a class with a `List<T>`, so it can't be a component. It lives on the heap.

### Components

```csharp
// 4-byte handle, stored inline in the component
public partial struct Trail : IEntityComponent
{
    public UniquePtr<TrailHistory> Value;
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

The scene initializer creates each follower and allocates its own `TrailHistory` blob. `UniquePtr.Alloc` returns a 4-byte handle that goes straight onto the component:

```csharp
for (int i = 0; i < _followerCount; i++)
{
    float phase = (float)i / _followerCount * 2f * math.PI;
    var trailPtr = UniquePtr.Alloc(_world.Heap, new TrailHistory { MaxLength = 60 });

    _world.AddEntity<PatrolTags.Follower>()
        .Set(new Position(PatrolMovementSystem.FigureEightAt(phase)))
        .Set(new PathPhase(phase))
        .Set(new Trail { Value = trailPtr });
}
```

Each entity owns its own trail, so `UniquePtr<T>` is the right type — no refcounting, single owner.

## Movement system

The system advances each follower along the figure-8 and appends to its trail. `trail.Value.Get(World)` returns the live `TrailHistory` instance — mutating it sticks because we're holding the object reference:

```csharp
public partial class PatrolMovementSystem : ISystem
{
    const float Speed = 1.5f;

    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Position position, ref PathPhase phase, in Trail trail)
    {
        phase.Value = (phase.Value + Speed * World.DeltaTime) % (2f * math.PI);
        position.Value = FigureEightAt(phase.Value);

        var trailHistory = trail.Value.Get(World);
        trailHistory.Positions.Add((Vector3)position.Value);

        while (trailHistory.Positions.Count > trailHistory.MaxLength)
            trailHistory.Positions.RemoveAt(0);
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

## Concepts introduced

- **`UniquePtr<T>`** — single-owner managed pointer. Stored inline as a 4-byte handle on the component. See [Heap](../advanced/heap.md).
- **`UniquePtr.Alloc(World.Heap, value)`** — static factory; mirrors `UniquePtr.Alloc(World, value)` if you have the `WorldAccessor` handy.
- **`ptr.Get(World)`** — dereferences to the managed object reference.
- **`ptr.Dispose(World)`** — returns the slot to the pool.
- **`OnRemoved` cleanup observer** — the canonical way to release pointers when entities disappear. See [Heap — cleanup is manual](../advanced/heap.md#cleanup-is-manual-for-entity-owned-pointers) and [Entity Events](../entity-management/entity-events.md).
- For sharing the same managed data across many entities, see `SharedPtr<T>` in [Sample 15 — Blob Storage](15-blob-storage.md).
- For Burst-compatible variants used inside jobs, see [Native Pointers](13-native-pointers.md).
