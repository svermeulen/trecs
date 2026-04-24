# 10 — Pointers

Using heap pointers to store managed data (classes, lists, arrays) that can't live in unmanaged components.

**Source:** `Samples/10_Pointers/`

## What It Does

Entities follow shared patrol routes (displayed as waypoints). Each entity has its own trail history (displayed as a LineRenderer). Multiple entities share the same route data via `SharedPtr`, while each has a unique trail via `UniquePtr`.

## Schema

### Managed Classes (Heap Data)

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

These contain `List<T>` — managed types that can't be stored directly in components.

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

### SharedPtr — Shared Route Data

```csharp
// Allocate once
SharedPtr<PatrolRoute> routePtr = world.Heap.AllocShared(new PatrolRoute
{
    Waypoints = waypoints,
    Color = Color.red,
    Speed = 2f
});

// First entity gets the original
world.AddEntity<PatrolTags.Follower>()
    .Set(new Route { Value = routePtr })
    .Set(new Trail { Value = world.Heap.AllocUnique(new TrailHistory { ... }) });

// Second entity clones (increments ref count, shares same data)
world.AddEntity<PatrolTags.Follower>()
    .Set(new Route { Value = routePtr.Clone(world.Heap) })
    .Set(new Trail { Value = world.Heap.AllocUnique(new TrailHistory { ... }) });
```

### UniquePtr — Per-Entity Trail

```csharp
UniquePtr<TrailHistory> trailPtr = world.Heap.AllocUnique(new TrailHistory
{
    Positions = new List<Vector3>(),
    MaxLength = 100
});
```

Each entity gets its own `UniquePtr` — the data is not shared.

## Systems

### PatrolMovementSystem

Reads the shared route and unique trail:

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

Pointers must be disposed when entities are removed:

```csharp
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

## Concepts Introduced

- **`SharedPtr<T>`** — reference-counted pointer for shared managed data
- **`UniquePtr<T>`** — single-owner pointer for per-entity managed data
- **`Clone()`** — increments ref count on shared pointers
- **`Dispose()`** — decrements ref count (shared) or frees (unique)
- **`Get(World)`** — dereferences a pointer to access the data
- **Cleanup handlers** — dispose pointers when entities are removed to prevent leaks
