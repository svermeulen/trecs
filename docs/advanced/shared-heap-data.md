# Shared heap data

Some heap data — palettes, loot tables, animation curves, baked navmeshes — is referenced by many entities and needs to live once, shared, and be released when the last reference disappears. This article covers the patterns for managing that data: how to keep a shared blob alive, how spawners reference it, and how stable identity interacts with snapshots and replay.

For the underlying pointer mechanics (`SharedPtr<T>`, `Clone`, `Dispose`), see [Heap](heap.md).

## BlobId is required

Every `SharedPtr.Alloc` (and `NativeSharedPtr.Alloc`) call takes a caller-supplied `BlobId`. There's no auto-mint overload — shared blobs are always addressed by a stable identifier, both so independent call sites can resolve to the same allocation and so snapshots and recordings can round-trip the reference.

```csharp
public static class PaletteIds
{
    public static readonly BlobId Warm = new(1001);
    public static readonly BlobId Cool = new(1002);
}
```

See [Choosing `BlobId` values](#choosing-blobid-values) at the bottom of this page for how to pick the numeric value.

## The seeder

A *seeder* is a long-lived object that allocates each blob once at startup and holds the returned `SharedPtr<T>` as a member. Without that anchor, the refcount would drop to zero between init and the first entity spawn — the heap evicts blobs whose refcount hits zero, and the next lookup would fail.

```csharp
public class PaletteSeeder
{
    readonly World _world;
    SharedPtr<ColorPalette> _warm;
    SharedPtr<ColorPalette> _cool;

    public PaletteSeeder(World world) => _world = world;

    public void Initialize()
    {
        var world = _world.CreateAccessor(AccessorRole.Unrestricted);
        _warm = SharedPtr.Alloc(world.Heap, PaletteIds.Warm, BuildWarm());
        _cool = SharedPtr.Alloc(world.Heap, PaletteIds.Cool, BuildCool());
    }

    public void Dispose()
    {
        var world = _world.CreateAccessor(AccessorRole.Unrestricted);
        _warm.Dispose(world.Heap);
        _cool.Dispose(world.Heap);
    }
}
```

That's enough to keep the blobs alive. The next question is how entity spawners get their own handles to these blobs. There are two patterns.

## Pattern A — clone from a provider

If the shared assets are a small, fixed, named set, expose the seeder's handles through a typed provider and have spawners `Clone` them:

```csharp
public class PaletteProvider
{
    SharedPtr<ColorPalette> _warm;
    SharedPtr<ColorPalette> _cool;

    public void Initialize(WorldAccessor world)
    {
        _warm = SharedPtr.Alloc(world.Heap, PaletteIds.Warm, BuildWarm());
        _cool = SharedPtr.Alloc(world.Heap, PaletteIds.Cool, BuildCool());
    }

    public SharedPtr<ColorPalette> NewWarmHandle(WorldAccessor world) => _warm.Clone(world.Heap);
    public SharedPtr<ColorPalette> NewCoolHandle(WorldAccessor world) => _cool.Clone(world.Heap);

    public void Dispose(WorldAccessor world)
    {
        _warm.Dispose(world.Heap);
        _cool.Dispose(world.Heap);
    }
}
```

Spawners depend on `PaletteProvider`, not on `BlobId` constants:

```csharp
world.AddEntity<MyTag>()
    .Set(new PaletteRef { Value = palettes.NewWarmHandle(world) });
```

This is the most idiomatic shape for a fixed set: type-safe, discoverable in IntelliSense, no integer identifiers leaking into spawn code, no manual ID registry to maintain. `Clone` bumps the refcount the same way an ID lookup would, so the runtime behaviour is identical.

## Pattern B — look up by stable `BlobId`

If you give each blob an explicit `BlobId` when seeding, any code can resolve it later without holding a reference to the provider:

```csharp
// Seeder (two-arg Alloc seeds the blob)
_warm = SharedPtr.Alloc(world.Heap, PaletteIds.Warm, BuildWarm());

// Spawner — no provider injected. One-arg Alloc looks up by ID.
world.AddEntity<MyTag>()
    .Set(new PaletteRef
    {
        Value = SharedPtr.Alloc<ColorPalette>(world.Heap, PaletteIds.Warm),
    });
```

The single-argument `SharedPtr.Alloc<T>(heap, blobId)` finds the existing blob, bumps its refcount, and returns a fresh handle. It's `Clone` addressed by ID instead of by reference.

This pattern is *necessary*, not merely preferable, in two cases:

- **Content-pipeline assets** where IDs are assigned by an importer and baked into level data.
- **Snapshot reload and desync recovery.** When a snapshot captures an entity's `SharedPtr<T>`, what gets serialized is the `BlobId`. On reload — possibly into a process that started up differently, or on a peer that diverged mid-game — the heap must contain a blob under that exact ID for the pointer to resolve. Stable, hand-authored IDs make this work even if init code was refactored, reordered, or skipped between save and load.

## Choosing `BlobId` values

`BlobId` wraps a 64-bit `long`. Whichever value you pick goes behind a named constant, so call sites all read the same (`PaletteIds.Warm`). The question is what to put on the right-hand side:

```csharp
public static readonly BlobId Warm = new(/* ??? */);
```

Practical options:

- **Random 64-bit literals** — `new(0x7f3a9b21d4e6c5a8)`. Generate once at authoring time, paste in, never change. Effectively zero collision risk with anything else in the heap, including IDs from other modules or plugins. The downside is the literal itself isn't human-meaningful — but since you read it through the named constant, that rarely matters in practice. A reasonable default.
- **Stable string hashes** — `new(StableHash64("warm-palette"))`. Same collision profile as random literals at 64 bits, with the bonus that the value is derivable from the name. Useful when IDs need to round-trip through text (config files, save formats) or when you want the source of truth to be the string rather than the literal.
- **Asset-pipeline IDs** — GUIDs or content hashes the importer already produced, cast or hashed down to `long`. The right answer when the blob originates from a content pipeline; the `BlobId` design is built for this case.
- **Hand-assigned small ints** — `new(1001)`, `new(1002)`, … Simplest for a single registry in a single codebase. The drawback is brittleness in multi-module setups: if two independent codebases both start their registries at `1001`, they collide on shared blob stores. Fine if you control all the code that mints stable IDs; reach for one of the wider-range options if you don't.
