# 13 — Fixed Array

Inline bounded collections on a component — no heap pointer, no allocator, no cleanup handler. The whole component stays blittable and travels straight into a Burst job.

**Source:** `com.trecs.core/Samples~/Tutorials/13_FixedArray/`

## What it does

Followers chase a hard-coded figure-8 path and leave a fading trail of their recent positions. Each follower's trail is a [`FixedArray32<float3>`](../advanced/fixed-collections.md#fixedarrayn) stored **directly on the component** — 32 inline slots, used as a ring buffer.

Reach for `FixedArray<N>` whenever a per-entity collection has a known small upper bound. The storage lives inside the component, so:

- No heap allocation on entity creation.
- No `OnRemoved` cleanup handler — the inline bytes vanish with the entity.
- No serializer registration — the array round-trips through snapshot save / load as part of the component bytes.
- The component stays blittable, so a `[WrapAsJob]` Burst job can mutate it without any pointer resolution.

The trade-off is up-front cost: every follower pays for all 32 slots, used or not.

## Schema

```csharp
using Trecs.Collections;
using Unity.Mathematics;

public partial struct Trail : IEntityComponent
{
    public FixedArray32<float3> Positions;
    public int Head;
    public int Count;
}
```

- `Positions` — the inline storage; 32 slots × `sizeof(float3)` = 384 bytes baked into the component.
- `Head` — index of the **oldest** live position (the read end of the ring buffer).
- `Count` — number of live positions, in `0..32`.

The next write slot is `(Head + Count) % Capacity`. The buffer is empty when `Count == 0` and full when `Count == 32`. This representation has no empty/full ambiguity — no reserved sentinel slot needed.

```csharp
[Unwrap]
public partial struct PathPhase : IEntityComponent
{
    public float Value;
}
```

## Initialization

No allocation step. The `Trail` component starts zeroed — `Head = Count = 0` is an empty ring buffer — and is populated by the movement system over time:

```csharp
_world.AddEntity<PatrolTags.Follower>()
    .Set(new Position(PatrolMovementSystem.FigureEightAt(phase)))
    .Set(new PathPhase(phase));
```

Compare with Sample 10's managed `UniquePtr<Queue<Vector3>>`, which had to allocate a `Queue` per entity at template instantiation and register an `OnRemoved` handler to free it.

## Movement system — Burst job

```csharp
public partial class PatrolMovementSystem : ISystem
{
    const float Speed = 1.5f;

    [ForEachEntity(typeof(PatrolTags.Follower))]
    [WrapAsJob]
    static void Execute(
        ref Position position,
        ref PathPhase phase,
        ref Trail trail,
        in NativeWorldAccessor world)
    {
        phase.Value = (phase.Value + Speed * world.DeltaTime) % (2f * math.PI);
        position.Value = FigureEightAt(phase.Value);

        int capacity = trail.Positions.Length;
        int writeIndex = (trail.Head + trail.Count) % capacity;
        trail.Positions.Mut(writeIndex) = position.Value;

        if (trail.Count < capacity)
            trail.Count++;
        else
            trail.Head = (trail.Head + 1) % capacity;
    }
}
```

The ring-buffer push runs in O(1). Compare with the obvious "shift-everything-left" approach (`RemoveAt(0)` + `Add`), which is O(N) per follower per fixed tick — at 32 slots and a few hundred followers that's measurable wasted work every frame.

`[WrapAsJob]` works without ceremony because the whole `Trail` component is blittable. There's no heap pointer to resolve, no `Read` / `Write` wrapper to take, and no `AtomicSafetyHandle` to thread through — the job reads and writes the component's bytes directly.

## Renderer — main thread

The presenter walks the ring buffer from oldest to newest:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
[ExecuteAfter(typeof(PatrolMovementSystem))]
public partial class PatrolPresenter : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Position position, in Trail trail, in GameObjectId goId)
    {
        var go = _goManager.Resolve(goId);
        go.transform.position = (Vector3)position.Value;

        var lineRenderer = go.GetComponent<LineRenderer>();
        lineRenderer.positionCount = trail.Count;

        int capacity = trail.Positions.Length;
        for (int i = 0; i < trail.Count; i++)
            lineRenderer.SetPosition(i, (Vector3)trail.Positions[(trail.Head + i) % capacity]);
    }
}
```

`trail.Positions[i]` returns `ref readonly float3` — zero-copy reads, including through the `in Trail` parameter. Writes go through `Positions.Mut(i)`, which isn't callable through an `in` reference — see the [read/write split](../advanced/fixed-collections.md#read-write-split) section in the Fixed Collections guide.

## Concepts introduced

- **`FixedArray<N><T>`** — size-specialized inline array from `Trecs.Collections`. Available sizes: 2, 4, 8, 16, 32, 64, 128, 256. See [Fixed Collections](../advanced/fixed-collections.md) for the full picture and how it compares to `FixedList<N>` and Unity's `FixedList*Bytes`.
- **Ring-buffer pattern** — `(Head + i) % Capacity` indexing avoids the O(N) shift cost of `RemoveAt(0)` on every append.
- **Blittable component → Burst-direct mutation** — when a component is fully unmanaged (no managed refs, no heap pointers), `[WrapAsJob]` reads and writes its bytes directly with no resolver / wrapper plumbing.
- **No cleanup handler needed** — inline storage is freed with the entity. Compare with [Sample 10](10-pointers.md), which registers an `OnRemoved` observer to dispose its `UniquePtr`.

## When to reach for something else

- **The upper bound varies widely across entities, or usually sits far below the cap.** A `FixedArray256<T>` that's typically empty wastes storage on every entity. Use a heap pointer to a `NativeList<T>` or `List<T>` instead — see [Pointers](../experimental/pointers.md).
- **The count is variable but every slot still gets used.** Use [`FixedList<N>`](../advanced/fixed-collections.md#fixedlistn): same inline storage, plus a `Count` field and `Add` / `RemoveAt` / `RemoveAtSwapBack` helpers.
- **The data must be shared across many entities.** Use `SharedPtr<T>` / `NativeSharedPtr<T>` — see [Sample 15 — Blob Seed Pattern](15-blob-seed-pattern.md).
