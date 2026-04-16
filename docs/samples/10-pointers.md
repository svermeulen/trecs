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
public partial struct CRoute : IEntityComponent
{
    public SharedPtr<PatrolRoute> Value;  // Shared across entities
    public float Progress;
}

public partial struct CTrail : IEntityComponent
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
    .Set(new CRoute { Value = routePtr })
    .Set(new CTrail { Value = world.Heap.AllocUnique(new TrailHistory { ... }) })
    .AssertComplete();

// Second entity clones (increments ref count, shares same data)
world.AddEntity<PatrolTags.Follower>()
    .Set(new CRoute { Value = routePtr.Clone(world.Heap) })
    .Set(new CTrail { Value = world.Heap.AllocUnique(new TrailHistory { ... }) })
    .AssertComplete();
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
void Execute(ref Position position, in CRoute route, in CTrail trail)
{
    // Read shared route data
    var patrolRoute = route.Value.Get(World);

    // Compute position along waypoints
    float totalProgress = route.Progress + World.ElapsedTime * patrolRoute.Speed;
    position.Value = InterpolateWaypoints(patrolRoute.Waypoints, totalProgress);

    // Write to unique trail
    var trailHistory = trail.Value.Get(World);
    trailHistory.Positions.Add(position.Value);
    while (trailHistory.Positions.Count > trailHistory.MaxLength)
        trailHistory.Positions.RemoveAt(0);
}
```

## Cleanup

Pointers must be disposed when entities are removed:

```csharp
world.Events.InGroupsWithTags<PatrolTags.Follower>()
    .OnRemoved((Group group, EntityRange indices) =>
    {
        for (int i = indices.Start; i < indices.End; i++)
        {
            var entityIndex = new EntityIndex(i, group);

            var route = world.Component<CRoute>(entityIndex).Read;
            route.Value.Dispose(world);

            var trail = world.Component<CTrail>(entityIndex).Read;
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
