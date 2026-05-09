# Heap Allocation Rules

!!! note "When you need this page"
    Read this if you're **allocating heap pointers from non-`Fixed` code** (input systems, services, init hooks), if you need **stable `BlobId`s for shared assets** (palettes, animation curves, anything content-pipeline-driven), or if you're **debugging a "cannot allocate" assertion**. The basic mechanics of `SharedPtr` / `UniquePtr` are on the [Heap](heap.md) page.

This page covers when and where you're allowed to allocate heap pointers and create entities. The rules exist because Trecs is built around deterministic simulation — for replay and snapshot/restore to work, the same code must produce the same world state on every run.

Pointer mechanics themselves (`SharedPtr<T>`, `UniquePtr<T>`, native variants) are on the [Heap](heap.md) page. Per-role permissions for non-heap concerns (components, structural changes, RNG) are on [Accessor Roles](accessor-roles.md).

## Heap allocation by role

Every `WorldAccessor` carries an [`AccessorRole`](accessor-roles.md) that controls what kinds of heap allocation it can perform. Input-system accessors (system-owned accessors created from `[ExecuteIn(SystemPhase.Input)]`) carry `Variable` plus an internal input flag — shown as "Input system" below.

| Accessor | Persistent (`AllocShared`, `AllocUnique`, native variants) | Frame-scoped (`AllocSharedFrameScoped`, etc.) |
|------|--------|--------|
| `Fixed` | ✅ Allowed | ❌ Not allowed |
| Input system | ❌ Not allowed — use the FrameScoped variant | ✅ Allowed |
| `Variable` | ❌ Not allowed | ❌ Not allowed |
| `Unrestricted` | ✅ Allowed | ✅ Allowed |

Violations throw an immediate `TrecsException` at the allocation site rather than producing silent desync later. The input-system error message points at the `FrameScoped` variant explicitly, since that's almost always what the caller wanted.

The shape: persistent allocations participate in deterministic ID minting (next section), so only deterministic-state pickers (`Fixed`, `Unrestricted`) may make them; frame-scoped allocations exist for input-side transient payloads, so only input-system and `Unrestricted` accessors may make them. `Variable` accessors can do neither.

Entity creation (`AddEntity` / `RemoveEntity` / `MoveTo`) follows the same pattern: `Fixed` and `Unrestricted` for non-VUO templates; `Variable` / input-system / `Unrestricted` for `[VariableUpdateOnly]` templates. See [Accessor Roles](accessor-roles.md#capability-matrix).

### Why presentation-phase systems are excluded

Heap IDs are drawn from a single deterministic RNG shared across init and all fixed systems. If presentation systems could mint IDs, they'd either pollute that stream or need a non-deterministic fallback — both break replay. Same logic for structural changes: simulation-state templates participate in the snapshot checksum, and presentation systems run at non-deterministic per-render-frame cadence.

`[VariableUpdateOnly]` templates are the exception (cameras, view-only helpers): their groups are render-cadence state, skipped from the checksum, so presentation- and input-phase systems *can* spawn entities of a VUO template. They still can't allocate heap.

If you want to allocate heap or spawn a non-VUO entity from a presentation system, the data probably belongs in a fixed component, a `[VariableUpdateOnly]` component populated from fixed state, or a dedicated `[VariableUpdateOnly]` template.

## ID minting — how auto-IDs work

Every allocation has a `BlobId`. The `AllocShared(T blob)` overload (and friends) mints one for you by drawing from a single deterministic RNG shared across init and all fixed systems, seeded from `WorldSettings.RandomSeed`. As long as the calling code is itself deterministic, the IDs assigned by auto-`AllocShared` are deterministic too — and snapshots and replays work transparently.

The discipline is the same as fixed-update systems: **init and fixed code must be deterministic.** Conditional allocation based on `UnityEngine.Random`, `DateTime.Now`, filesystem ordering, etc. changes the IDs between runs the same way it would desync a fixed-update system. When you can't satisfy that, use explicit `BlobId`s (next section).

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

- **Persistent allocation from init or `Fixed` systems** (and `Unrestricted`-role accessors if you must). Input systems use `AllocXxxFrameScoped` for transient input payloads instead; `Variable`-cadence systems can't allocate at all.
- **Auto-IDs are deterministic** when init and fixed code is deterministic — same rule that already applies for replay.
- **Use explicit `BlobId`s** when you want stable identity independent of startup ordering — particularly for content-pipeline assets.
- **Seeder pattern**: a long-lived class holds `SharedPtr<T>` members for shared assets, allocated once at init with a stable `BlobId`, looked up by entities via `AllocShared(BlobId)`.
- **Always dispose** pointers stored on components from an `OnRemoved` observer.
- **Presentation systems read; they don't allocate heap, and they only spawn `[VariableUpdateOnly]` templates.** If a presentation system needs new sim-state, populate it from a `[VariableUpdateOnly]` component or a fixed system; if it needs new render-only entities, declare the template `[VariableUpdateOnly]` and spawn from there.
