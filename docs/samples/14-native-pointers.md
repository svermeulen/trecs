# 14 — Native Pointers

Using Burst-compatible heap pointers to store unmanaged data that a parallel job can read and mutate.

**Source:** `Samples/14_NativePointers/`

## What it does

Mirrors [Sample 10](10-pointers.md) — entities follow shared patrol routes and each draws a trail of recent positions — but the route and trail payloads are now **unmanaged structs**, allocated in native heap blobs via `NativeSharedPtr` / `NativeUniquePtr`, and the movement system runs as a Burst-compiled job that resolves the pointers inline.

This is the shape you reach for whenever you want heap-allocated data in a Burst job. The managed `SharedPtr` / `UniquePtr` from Sample 10 cannot be used in jobs at all — that is the gap these native variants fill.

## Schema

### Unmanaged payloads

```csharp
public readonly struct PatrolRoute
{
    public readonly FixedList64<float3> Waypoints;
    public readonly Color Color;
    public readonly float Speed;

    public PatrolRoute(in FixedList64<float3> waypoints, Color color, float speed)
    {
        Waypoints = waypoints;
        Color = color;
        Speed = speed;
    }
}

public struct TrailHistory
{
    public FixedList64<float3> Positions;
    public int MaxLength;
}
```

Both are unmanaged. `PatrolRoute` is a `readonly struct` because the route never mutates after construction — this matches the framework's own pointer types (`NativeSharedPtr`, `NativeUniquePtr`) and avoids the defensive copies the compiler would otherwise insert when the struct is accessed through an `in` reference. `TrailHistory` is mutable so the movement job can append to `Positions` each tick.

The waypoints and positions use [`FixedList64<T>`](../advanced/fixed-collections.md) from `Trecs.Collections` — an inline, bounded list with 64 element slots. Unlike a managed `List<T>`, it's unmanaged and lives inside the blob directly.

### Components

```csharp
public partial struct Route : IEntityComponent
{
    public NativeSharedPtr<PatrolRoute> Value;  // Shared across followers of the same route
    public float Progress;
}

public partial struct Trail : IEntityComponent
{
    public NativeUniquePtr<TrailHistory> Value;  // Unique per entity
}
```

The pointer handles are small value types (12 bytes and 4 bytes respectively) stored inline in the component.

## Allocation

### NativeSharedPtr — shared route data

```csharp
var waypoints = new FixedList64<float3>();
// ... fill waypoints ...

var routePtr = world.Heap.AllocNativeShared(
    new PatrolRoute(waypoints, Color.cyan, speed: 2f));

// Each follower clones the pointer — refcount increments,
// all clones point at the same blob.
for (int i = 0; i < count; i++)
{
    var routeClone = routePtr.Clone(world);
    world.AddEntity<NativePatrolTags.Follower>()
        .Set(new Route { Value = routeClone, Progress = i })
        .Set(...);
}

// Dispose the original — refcount drops by one. Blob stays alive
// because entity clones still reference it.
routePtr.Dispose(world);
```

### NativeUniquePtr — per-entity trail

```csharp
var trailPtr = world.Heap.AllocNativeUnique(
    new TrailHistory { MaxLength = 40 });
```

Each entity gets its own `NativeUniquePtr` — no sharing, no refcount.

## Systems

### PatrolMovementSystem — Burst job

This is the point of the sample: the pointer resolution happens inside a Burst-compiled job.

```csharp
public partial class PatrolMovementSystem : ISystem
{
    [ForEachEntity(typeof(NativePatrolTags.Follower))]
    [WrapAsJob]
    static void Execute(
        ref Position position,
        in Route route,
        ref Trail trail,
        in NativeWorldAccessor world)
    {
        // Read the shared route through NativeWorldAccessor — Burst-safe.
        ref readonly var patrolRoute = ref route.Value.Get(world);
        ref readonly var waypoints = ref patrolRoute.Waypoints;

        float totalProgress = route.Progress + world.ElapsedTime * patrolRoute.Speed;
        float wrappedProgress = totalProgress % waypoints.Count;

        int indexA = (int)wrappedProgress;
        int indexB = (indexA + 1) % waypoints.Count;
        float t = wrappedProgress - indexA;
        position.Value = math.lerp(waypoints[indexA], waypoints[indexB], t);

        // Mutate the unique trail. GetMut is a `ref this` extension method,
        // so the component holding the pointer must itself be accessed by ref.
        ref var trailData = ref trail.Value.GetMut(world);
        if (trailData.Positions.Count >= trailData.MaxLength)
            trailData.Positions.RemoveAt(0);
        trailData.Positions.Add(position.Value);
    }
}
```

Key points:

- **`[WrapAsJob]`** turns this static method into a Burst-compiled parallel job. The source generator wires up the `NativeWorldAccessor` automatically — no manual job struct needed.
- **`route.Value.Get(world)`** resolves the `NativeSharedPtr` through `NativeWorldAccessor.SharedPtrResolver`. Returns `ref T` to the blob contents.
- **`trail.Value.GetMut(world)`** is the **mutable** counterpart for `NativeUniquePtr`. It's an extension method with `ref this`, which forces the caller to hold a writable reference to the component — the framework uses this to track write dependencies without any extra bookkeeping.
- The movement system uses `ref Trail trail` so that `trail.Value.GetMut(...)` is callable. `in Route route` is enough for the shared side because we only read.

### PatrolRendererSystem — main thread

```csharp
[ExecuteIn(SystemPhase.Presentation)]
[ExecuteAfter(typeof(PatrolMovementSystem))]
public partial class PatrolRendererSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Position position, in Trail trail, in GameObjectId goId)
    {
        var go = _registry.Resolve(goId);
        go.transform.position = (Vector3)position.Value;

        ref readonly var trailData = ref trail.Value.Get(World);
        var lineRenderer = go.GetComponent<LineRenderer>();
        lineRenderer.positionCount = trailData.Positions.Count;
        for (int i = 0; i < trailData.Positions.Count; i++)
            lineRenderer.SetPosition(i, (Vector3)trailData.Positions[i]);
    }
}
```

The same pointer resolves on the main thread via the `WorldAccessor` overload of `Get(...)`. No conversion is required.

## Cleanup

Native pointers stored in components must be disposed when entities are removed, same as their managed counterparts — otherwise the blobs leak and a warning fires on world disposal.

```csharp
public partial class PointerCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container

    public PointerCleanupHandler(World world)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);

        World.Events.EntitiesWithTags<NativePatrolTags.Follower>()
            .OnRemoved(OnFollowerRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnFollowerRemoved(in Route route, in Trail trail)
    {
        route.Value.Dispose(World);   // NativeSharedPtr: decrement refcount
        trail.Value.Dispose(World);   // NativeUniquePtr: release blob
    }

    public void Dispose() => _disposables.Dispose();
}
```

The cleanup handler is registered in the composition root *before* the scene initializer spawns entities, so it also catches removals that happen during world disposal.

## Concepts introduced

- **`NativeSharedPtr<T>`** — Burst-compatible reference-counted pointer to an unmanaged blob. Mirrors `SharedPtr<T>` from Sample 10.
- **`NativeUniquePtr<T>`** — Burst-compatible single-owner pointer. Mirrors `UniquePtr<T>`.
- **`AllocNativeShared` / `AllocNativeUnique`** on `HeapAccessor` — allocate unmanaged blobs on the native heap.
- **`Get(NativeWorldAccessor)` / `GetMut(NativeWorldAccessor)`** — resolve a native pointer from inside a Burst job. The same pointers resolve from the main thread via the `WorldAccessor` overload.
- **`[WrapAsJob]` + auto-injected `NativeWorldAccessor`** — generates a Burst job whose `Execute` has the native world accessor wired up automatically.
- **`FixedList64<T>`** — inline bounded list, Burst-compatible, used here to store waypoints and trail positions inside unmanaged blobs.
- **`readonly struct`** for shared blob payloads — avoids defensive copies when read through `in` references.
