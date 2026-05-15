# 13 — Native Pointers

Burst-compatible heap pointers — unmanaged data that a parallel job can read and mutate inline.

**Source:** `com.trecs.core/Samples~/Tutorials/13_NativePointers/`

## What it does

Mirrors [Sample 10](10-pointers.md): followers trace a figure-8 path, each leaving a fading trail. The difference is the trail data is now an **unmanaged struct** allocated via `NativeUniquePtr<TrailHistory>`, and the movement system is a Burst-compiled `[WrapAsJob]` job that resolves the pointer inline.

Reach for the native variants whenever you want heap-allocated data inside a Burst job. The managed `UniquePtr` / `SharedPtr` from Sample 10 / 15 are main-thread only — these native variants fill the Burst-side gap.

## Schema

### Unmanaged payload

```csharp
using Trecs.Collections;

public struct TrailHistory
{
    public FixedList64<float3> Positions;
    public int MaxLength;
}
```

Every field is unmanaged, so the blob can live in Trecs's native heap. `Positions` uses [`FixedList64<T>`](../advanced/fixed-collections.md) — an inline bounded list with 64 slots. For longer trails pick a larger `FixedListN`, or switch to a heap-allocated container.

### Components

```csharp
public partial struct Trail : IEntityComponent
{
    public NativeUniquePtr<TrailHistory> Value;
}

[Unwrap]
public partial struct PathPhase : IEntityComponent
{
    public float Value;
}
```

`NativeUniquePtr<T>` is a 4-byte handle stored inline in the component.

## Allocation

```csharp
for (int i = 0; i < _followerCount; i++)
{
    float phase = (float)i / _followerCount * 2f * math.PI;

    var trailPtr = NativeUniquePtr.Alloc<TrailHistory>(
        _world.Heap,
        new TrailHistory { MaxLength = 40 });

    _world.AddEntity<NativePatrolTags.Follower>()
        .Set(new Position(PatrolMovementSystem.FigureEightAt(phase)))
        .Set(new PathPhase(phase))
        .Set(new Trail { Value = trailPtr });
}
```

Each follower owns its own `NativeUniquePtr` — no sharing, no refcount.

## Movement system — Burst job

The whole point: pointer resolution and mutation happen inside a Burst-compiled job. `[WrapAsJob]` generates the job struct; `Write(world.UniquePtrResolver)` returns a safety-checked wrapper with `.Value` as a `ref T` into the blob.

```csharp
public partial class PatrolMovementSystem : ISystem
{
    const float Speed = 1.5f;

    [ForEachEntity(typeof(NativePatrolTags.Follower))]
    [WrapAsJob]
    static void Execute(
        ref Position position,
        ref PathPhase phase,
        ref Trail trail,
        in NativeWorldAccessor world)
    {
        phase.Value = (phase.Value + Speed * world.DeltaTime) % (2f * math.PI);
        position.Value = FigureEightAt(phase.Value);

        ref var trailData = ref trail.Value.Write(world.UniquePtrResolver).Value;
        if (trailData.Positions.Count >= trailData.MaxLength)
            trailData.Positions.RemoveAt(0);
        trailData.Positions.Add(position.Value);
    }
}
```

Key points:

- **`[WrapAsJob]`** turns the static method into a Burst-compiled parallel job. The source generator wires the `NativeWorldAccessor` in — no manual job struct.
- **`trail.Value.Write(world.UniquePtrResolver)`** returns a `NativeUniqueWrite<T>` carrying an `AtomicSafetyHandle` so Unity's job-safety walker detects cross-job read/write conflicts on the same blob at schedule time. `.Value` is a `ref T` into the blob.
- The movement system takes `ref Trail trail`. `Write` is a `ref this` extension, so the component holding the pointer must itself be accessed by `ref` — same rule as [the gotcha for mutating native pointers](../guides/gotchas.md#mutating-a-nativeuniqueptrt-needs-write-access-to-the-owning-component).

## Renderer — main thread

The renderer reads the same pointer from the main thread. The wrapper is the same shape (`Read(world).Value`); only the resolver vs `WorldAccessor` overload differs:

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

        ref readonly var trailData = ref trail.Value.Read(World).Value;
        var lineRenderer = go.GetComponent<LineRenderer>();
        lineRenderer.positionCount = trailData.Positions.Count;
        for (int i = 0; i < trailData.Positions.Count; i++)
            lineRenderer.SetPosition(i, (Vector3)trailData.Positions[i]);
    }
}
```

## Cleanup

Native pointers stored in components must be disposed when entities are removed — same as the managed variant. Forgotten disposes leak the blob and trip the leak detector at world shutdown.

```csharp
public partial class PointerCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new();

    public PointerCleanupHandler(World world)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);

        World.Events.EntitiesWithTags<NativePatrolTags.Follower>()
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

## Concepts introduced

- **`NativeUniquePtr<T>`** — Burst-compatible single-owner pointer. The unmanaged sibling of `UniquePtr<T>` from Sample 10.
- **`NativeUniquePtr.Alloc(world.Heap, value)`** — static factory. The `Alloc(world, value)` overload also works.
- **`Read(...)` / `Write(...)` wrappers** — return `NativeUniqueRead<T>` / `NativeUniqueWrite<T>` exposing `.Value` as a `ref` / `ref readonly` to the blob. The wrappers carry an `AtomicSafetyHandle` so Unity catches cross-job conflicts at schedule time.
- **Resolver vs `WorldAccessor`** — pass `world.UniquePtrResolver` inside jobs, or pass the `WorldAccessor` directly on the main thread.
- **`[WrapAsJob]` + auto-injected `NativeWorldAccessor`** — generates a Burst job with the native accessor wired up automatically.
- **`FixedList64<T>`** — inline bounded list, Burst-compatible, used inside the unmanaged blob.

For shared (refcounted) native blobs, see `NativeSharedPtr<T>` and the seeder pattern in [Sample 15 — Blob Storage](15-blob-storage.md).
