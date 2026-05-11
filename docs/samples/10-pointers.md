# 10 — Pointers

Heap pointers for managed data (classes, lists, arrays) that can't live in unmanaged components.

**Source:** `com.trecs.core/Samples~/Tutorials/10_Pointers/`

## What it does

Entities follow shared patrol routes (waypoints). Each has its own trail history (a LineRenderer). Multiple entities share route data via `SharedPtr`; each has a unique trail via `UniquePtr`.

## Schema

### Managed classes (heap data)

```csharp
public class PatrolRoute
{
    public List<Vector3> Waypoints;
    public Color Color;
    public float Speed;
}

public class TrailHistory
{
    public List<Vector3> Positions;
    public int MaxLength;
}
```

These hold `List<T>` — managed types that can't live directly in components.

### Components

```csharp
public partial struct Route : IEntityComponent
{
    public SharedPtr<PatrolRoute> Value;  // Shared across entities
    public float Progress;
}

public partial struct Trail : IEntityComponent
{
    public UniquePtr<TrailHistory> Value;  // Unique per entity
}
```

## Allocation

`SharedPtr` and `UniquePtr` need a blob store. The sample uses the in-memory store; a disk-backed store is also available — see [Heap](../advanced/heap.md):

```csharp
.AddBlobStore(new BlobStoreInMemory(
    new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 }, null))
```

### SharedPtr — shared route data

`AllocShared` returns a handle with refcount 1. `Clone` increments the count; each entity stores its own clone. After spawning, the original is disposed — the entity clones keep the object alive:

```csharp
var routePtr = world.Heap.AllocShared(new PatrolRoute { /* ... */ });

for (int i = 0; i < count; i++)
{
    world.AddEntity<PatrolTags.Follower>()
        .Set(new Route { Value = routePtr.Clone(world), Progress = i })
        .Set(new Trail { Value = world.Heap.AllocUnique(new TrailHistory { MaxLength = 50 }) });
}

// Each entity holds its own clone — the original is no longer needed.
routePtr.Dispose(world);
```

### UniquePtr — per-entity trail

```csharp
var trailPtr = world.Heap.AllocUnique(new TrailHistory { MaxLength = 50 });
```

Each entity owns its `UniquePtr` — not shared, mutated freely by the owner.

## Systems

### PatrolMovementSystem

Reads the shared route and the unique trail:

```csharp
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position position, in Route route, in Trail trail)
{
    // Read shared route — same waypoint list for all followers of this route
    var patrolRoute = route.Value.Get(World);
    var waypoints = patrolRoute.Waypoints;

    // Compute position from elapsed time + initial offset
    float totalProgress = route.Progress + World.ElapsedTime * patrolRoute.Speed;
    float wrappedProgress = totalProgress % waypoints.Count;

    int indexA = (int)wrappedProgress;
    int indexB = (indexA + 1) % waypoints.Count;
    float t = wrappedProgress - indexA;

    position.Value = math.lerp((float3)waypoints[indexA], (float3)waypoints[indexB], t);

    // Record position in unique trail — each entity has its own list
    var trailHistory = trail.Value.Get(World);
    trailHistory.Positions.Add((Vector3)position.Value);
    while (trailHistory.Positions.Count > trailHistory.MaxLength)
        trailHistory.Positions.RemoveAt(0);
}
```

## Cleanup

Pointers must be disposed when entities are removed. Range-based `OnRemoved` provides a `GroupIndex` and an index range; reconstructing the per-entity index reads each component before storage is freed:

```csharp
using Trecs.Internal; // EntityIndex lives here — used at this layer only.

world.Events.EntitiesWithTags<PatrolTags.Follower>()
    .OnRemoved((GroupIndex group, EntityRange indices) =>
    {
        for (int i = indices.Start; i < indices.End; i++)
        {
            var entityIndex = new EntityIndex(i, group);

            var route = world.Component<Route>(entityIndex).Read;
            route.Value.Dispose(world);

            var trail = world.Component<Trail>(entityIndex).Read;
            trail.Value.Dispose(world);
        }
    })
    .AddTo(_eventDisposables);
```

## Concepts introduced

- **`SharedPtr<T>`** — reference-counted pointer for shared managed data. See [Heap](../advanced/heap.md).
- **`UniquePtr<T>`** — single-owner pointer for per-entity managed data.
- **`Clone()`** — increments the `SharedPtr` ref count.
- **`Dispose()`** — decrements the ref count (shared) or returns to the pool (unique).
- **`Get(world)`** — dereferences a pointer.
- **Cleanup handlers** — dispose pointers on entity removal to prevent leaks. See [Heap Allocation Rules](../advanced/heap-allocation-rules.md) and [Entity Events](../entity-management/entity-events.md).
- For Burst-compatible variants used inside jobs, see [Native Pointers](14-native-pointers.md).
