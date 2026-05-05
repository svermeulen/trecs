# Heap Allocation Rules

This article covers **when and where** you're allowed to allocate heap pointers and create entities, and how Trecs assigns stable IDs to allocations. These rules exist because Trecs is built around deterministic simulation — for replay, networked rollback, and snapshot/restore to work, the same code must produce the same world state on every run.

For an introduction to the pointer types themselves (`SharedPtr<T>`, `UniquePtr<T>`, native variants), see [Heap](heap.md).

## Phase rules at a glance

| Phase | Heap allocation | Entity creation |
|------|-----------------|-----------------|
| **Initialization** (no system context — e.g. `SceneInitializer`) | ✅ Allowed | ✅ Allowed |
| `Fixed` systems | ✅ Allowed | ✅ Allowed |
| `Input` systems | ❌ Not allowed | Use `AddInput<T>()` instead |
| `EarlyPresentation` / `Presentation` / `LatePresentation` systems | ❌ Not allowed | ❌ Not allowed |

Presentation-phase systems are intentionally restricted to **reading** simulation state and writing to render-only data (typically `[VariableUpdateOnly]` components). They cannot allocate from the heap or create entities — both would introduce non-deterministic state into the simulation.

The framework asserts these rules at the call site. Calling `world.Heap.AllocShared(...)` from a presentation-phase system throws an immediate, clear exception rather than producing silent desync later.

## Why presentation-phase systems can't allocate or spawn entities

Two reasons:

1. **Heap allocation IDs.** Trecs draws all allocation IDs from a single deterministic RNG (`_fixedRng`) shared across initialization and all fixed systems. If presentation-phase systems were allowed to mint IDs, they'd either pollute that stream (breaking determinism for fixed systems) or need a non-deterministic fallback (silently breaking replay for any handle stored in a fixed component).

2. **Entity creation is structural.** Adding an entity changes group counts and component arrays — that's part of the simulation state that gets serialized in snapshots and checked for desyncs in replay. Presentation systems run at a per-render-frame cadence that isn't deterministic across runs (frame rate varies), so structural changes from them would break those guarantees.

If you find yourself wanting to allocate or spawn from a presentation system, the data probably belongs in a fixed component or a `[VariableUpdateOnly]` component populated by the presentation system from existing fixed state.

## ID minting — how auto-IDs work

Every allocation has a `BlobId` that uniquely identifies the underlying blob. The `AllocShared(T blob)` overload (and friends) mints this ID for you by drawing from `_fixedRng`:

- `_fixedRng` is a single shared deterministic RNG owned by the world, seeded from `WorldSettings.RandomSeed`.
- Every initialization callback and every fixed-update system pulls from the same stream.
- Because of this, the IDs assigned by auto-`AllocShared` calls are **deterministic** as long as the calling code is itself deterministic.

This is the same determinism discipline that already applies to fixed-update systems. If your initialization logic conditionally allocates based on `UnityEngine.Random` or `DateTime.Now`, the auto-IDs change between runs — the same way fixed-update systems with non-deterministic code would desync. The rule is: **init and fixed code must be deterministic.**

When this is satisfied, snapshots and replays work transparently — the IDs assigned at startup match across runs because the same code runs in the same order against the same RNG seed.

## Stable BlobIds — when init isn't deterministic

If your initialization is non-deterministic (e.g. you load content based on filesystem ordering, asset bundle availability, or user choices), you can opt out of auto-ID minting by supplying explicit `BlobId`s:

```csharp
public static class AssetIds
{
    public static readonly BlobId WarmPalette = new(1001);
    public static readonly BlobId CoolPalette = new(1002);
}

// Seed with an explicit ID — no dependency on init determinism
SharedPtr<ColorPalette> ptr = world.Heap.AllocShared(
    AssetIds.WarmPalette,
    BuildWarmPalette()
);
```

The `BlobId` is just an opaque `long`. Pick any non-zero value; what matters is that the same identifier is used consistently across runs. Hand-authored IDs are particularly useful for content-pipeline patterns where content is identified by a stable name regardless of load order.

## The seeder pattern — shared assets across many entities

A common pattern: a single piece of immutable data (a palette, a loot table, an animation curve, a path) is referenced by many entities. The data should exist once, live as long as it has any references, and be released when the last reference disappears.

The shape:

```csharp
public class PaletteSeeder
{
    SharedPtr<ColorPalette> _warm;
    SharedPtr<ColorPalette> _cool;

    public void Initialize(WorldAccessor world)
    {
        // Seed each palette once. The seeder holds these as members so
        // the blobs stay alive even if no entities reference them yet.
        _warm = world.Heap.AllocShared(AssetIds.WarmPalette, BuildWarm());
        _cool = world.Heap.AllocShared(AssetIds.CoolPalette, BuildCool());
    }

    public void Dispose(WorldAccessor world)
    {
        _warm.Dispose(world.Heap);
        _cool.Dispose(world.Heap);
    }
}

// Spawner — entities reference the asset by stable ID
var ptr = world.Heap.AllocShared<ColorPalette>(AssetIds.WarmPalette);
world.AddEntity<MyTag>().Set(new PaletteRef { Value = ptr });
```

`AllocShared<T>(BlobId)` (the lookup-only overload) creates a new handle pointing at the existing blob and bumps its reference count. The blob stays alive as long as the seeder's handle plus any entity-owned handles are non-zero.

### Cleanup is manual

Pointers stored on components must be disposed when the entity is removed — Trecs does **not** auto-dispose. The standard pattern is an `OnRemoved` observer registered against the relevant tag:

```csharp
accessor.Events.EntitiesWithTags<MyTag>()
    .OnRemoved((group, indices, world) =>
    {
        var refs = world.ComponentBuffer<PaletteRef>(group).Read;
        for (int i = indices.Start; i < indices.End; i++)
        {
            refs[i].Value.Dispose(world.Heap);
        }
    });
```

See [Sample 10 — Pointers](../samples/10-pointers.md) for a complete reference implementation of this pattern.

## Cheat sheet

- **Allocate from init or `Fixed` systems.** Don't allocate from `Input`, `EarlyPresentation`, `Presentation`, or `LatePresentation` systems.
- **Auto-IDs are deterministic** when init and fixed code is deterministic — same rule that already applies for replay.
- **Use explicit `BlobId`s** when you want stable identity independent of startup ordering — particularly for content-pipeline assets.
- **Seeder pattern**: a long-lived class holds `SharedPtr<T>` members for shared assets, allocated once at init with a stable `BlobId`, looked up by entities via `AllocShared(BlobId)`.
- **Always dispose** pointers stored on components from an `OnRemoved` observer.
- **Presentation systems read; they don't allocate or spawn.** If a presentation system needs new state, populate it from a `[VariableUpdateOnly]` component or a fixed system.
