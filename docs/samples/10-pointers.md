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
public struct CRoute : IEntityComponent
{
    public SharedPtr<PatrolRoute> Route;  // Shared across entities
    public float Progress;
}

public struct CTrail : IEntityComponent
{
    public UniquePtr<TrailHistory> Trail;  // Unique per entity
}
```

## Allocation

### SharedPtr — Shared Route Data

```csharp
// Allocate once
SharedPtr<PatrolRoute> routePtr = World.Heap.AllocShared(new PatrolRoute
{
    Waypoints = waypoints,
    Color = Color.red,
    Speed = 2f
});

// First entity gets the original
ecs.AddEntity<FollowerTag>()
    .Set(new CRoute { Route = routePtr })
    .Set(new CTrail { Trail = World.Heap.AllocUnique(new TrailHistory { ... }) })
    .AssertComplete();

// Second entity clones (increments ref count, shares same data)
ecs.AddEntity<FollowerTag>()
    .Set(new CRoute { Route = routePtr.Clone(World.Heap) })
    .Set(new CTrail { Trail = World.Heap.AllocUnique(new TrailHistory { ... }) })
    .AssertComplete();
```

### UniquePtr — Per-Entity Trail

```csharp
UniquePtr<TrailHistory> trailPtr = World.Heap.AllocUnique(new TrailHistory
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
[ForEachEntity(Tag = typeof(FollowerTag))]
void Execute(in FollowerView follower)
{
    // Read shared route data
    PatrolRoute route = follower.Route.Get(World.Heap);

    // Advance along waypoints
    follower.Progress += route.Speed * World.FixedDeltaTime;
    follower.Position = InterpolateWaypoints(route.Waypoints, follower.Progress);

    // Write to unique trail
    TrailHistory trail = follower.Trail.Get(World.Heap);
    trail.Positions.Add(follower.Position);
    if (trail.Positions.Count > trail.MaxLength)
        trail.Positions.RemoveAt(0);
}
```

## Cleanup

Pointers must be disposed when entities are removed:

```csharp
World.Events.InGroupsWithTags<FollowerTag>()
    .OnRemoved((group, range, world) =>
    {
        for (int i = range.Start; i < range.End; i++)
        {
            var idx = new EntityIndex(i, group);
            world.Component<CRoute>(idx).Read.Route.Dispose(world.Heap);
            world.Component<CTrail>(idx).Read.Trail.Dispose(world.Heap);
        }
    });
```

## Concepts Introduced

- **`SharedPtr<T>`** — reference-counted pointer for shared managed data
- **`UniquePtr<T>`** — single-owner pointer for per-entity managed data
- **`Clone()`** — increments ref count on shared pointers
- **`Dispose()`** — decrements ref count (shared) or frees (unique)
- **`Get(World.Heap)`** — dereferences a pointer to access the data
- **Cleanup handlers** — dispose pointers when entities are removed to prevent leaks
