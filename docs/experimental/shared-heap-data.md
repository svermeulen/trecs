# Shared Heap Data

!!! warning "Experimental"
    The `SharedPtr` / `BlobPtr` / `BlobId` surface this page describes is experimental and may change in future 0.x releases.

Sometimes many entities need the same data — a color palette, a baked navmesh, a shared lookup table. Storing a copy on each entity wastes memory. Instead, allocate the data once on the **heap** and give each entity a lightweight `SharedPtr` handle that points to it. Reference counting frees the data when the last handle is disposed.

The lifecycle looks like this:

1. **Assign a stable ID** (`BlobId`) so the data can be found by name across sessions.
2. **Seed the data** — allocate it once at startup and hold a pinning handle so it isn't evicted.
3. **Hand out handles** — each entity gets a `SharedPtr` (or `NativeSharedPtr`) that points to the shared data.
4. **Dispose handles** when entities are removed. When the last handle is gone, the data is freed.

For the underlying pointer mechanics (`SharedPtr<T>`, `Clone`, `Dispose`), see [Pointers](pointers.md).

## BlobId — naming shared data

Every shared allocation needs a caller-supplied `BlobId` — a stable 64-bit identifier. This is what lets independent code find the same blob, and what gets serialized into snapshots so pointers survive save/load.

```csharp
public static class PaletteIds
{
    public static readonly BlobId Warm = new(1001);
    public static readonly BlobId Cool = new(1002);
}
```

See [Choosing `BlobId` values](#choosing-blobid-values) for how to pick the numeric value.

## Seeding — allocating shared data { #the-seeder }

A *seeder* allocates each blob once at startup and holds a pinning handle to keep it alive. Without that anchor, the cache evicts the blob before any entity references it.

The pinning handle can be either:

- **`BlobPtr<T>`** — cache-layer pin. Takes a `BlobCache`, independent of `World`. Best when the seeder's only job is to anchor data.
- **`SharedPtr<T>`** — ECS-layer refcount. Use when the seeder also hands out clones to spawners ([Pattern A](#pattern-a-clone-from-a-provider)).

A `BlobPtr` seeder:

```csharp
public class PaletteSeeder
{
    readonly BlobCache _blobCache;
    BlobPtr<ColorPalette> _warm;
    BlobPtr<ColorPalette> _cool;

    public PaletteSeeder(BlobCache blobCache) => _blobCache = blobCache;

    public void Initialize()
    {
        _warm = BlobPtr.Alloc(_blobCache, PaletteIds.Warm, BuildWarm());
        _cool = BlobPtr.Alloc(_blobCache, PaletteIds.Cool, BuildCool());
    }

    public void Dispose()
    {
        _warm.Dispose(_blobCache);
        _cool.Dispose(_blobCache);
    }
}
```

The equivalent `SharedPtr` shape (used in [Pattern A](#pattern-a-clone-from-a-provider)):

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
        _warm = SharedPtr.Alloc(world, PaletteIds.Warm, BuildWarm());
        _cool = SharedPtr.Alloc(world, PaletteIds.Cool, BuildCool());
    }

    public void Dispose()
    {
        var world = _world.CreateAccessor(AccessorRole.Unrestricted);
        _warm.Dispose(world);
        _cool.Dispose(world);
    }
}
```

Either keeps the blobs alive. The next question is how spawners get handles to them — see [Pattern A](#pattern-a-clone-from-a-provider) and [Pattern B](#pattern-b-look-up-by-stable-blobid) below.

## `[Immutable]` requirement { #two-adoption-paths-for-immutable }

`SharedPtr<T>` requires `T` to carry `[Trecs.Immutable]` (or be on the built-in allowlist — `string`, `Type`, etc.). Without it, post-`Alloc` mutation silently desyncs determinism since the `BlobCache` is not snapshotted with game state.

The attribute can go on either a class or an interface:

- **Class route** — `[Immutable] sealed class Foo`. TRECS126 structurally audits the class: every field `readonly`, no public setters, public field/property types in the "obviously immutable" set. Best for **small leaf types built via a single constructor** — palettes, content descriptors, lookup tables.
- **Interface route** — `[Immutable] interface IReadOnlyFoo`. Mark a read-only interface `[Immutable]` and parameterize `SharedPtr<T>` on it. The mutable concrete implements the read members but keeps its existing construction lifecycle. Best for **types that can't be reshaped around field-level immutability** — pool-allocated, deserialized in place, multi-pass builders.

### Interface route — worked example

```csharp
[Trecs.Immutable]
public interface IReadOnlyWorldRegion
{
    int RegionId { get; }
    float Radius { get; }
    IReadOnlyList<Vector3> Waypoints { get; }
    IReadOnlyList<IReadOnlyPortal> Portals { get; }
}

[Trecs.Immutable]
public interface IReadOnlyPortal
{
    Vector3 Position { get; }
    int LinkedRegionId { get; }
}

public sealed class WorldRegion : IReadOnlyWorldRegion
{
    public int RegionId;
    public float Radius;
    public List<Vector3> Waypoints = new();
    public List<Portal> Portals = new();

    int IReadOnlyWorldRegion.RegionId => RegionId;
    float IReadOnlyWorldRegion.Radius => Radius;
    IReadOnlyList<Vector3> IReadOnlyWorldRegion.Waypoints => Waypoints;
    // IReadOnlyList<out T> is covariant — Portal : IReadOnlyPortal
    // lets this upcast work with no copy or allocation.
    IReadOnlyList<IReadOnlyPortal> IReadOnlyWorldRegion.Portals => Portals;
}
```

Spawners parameterize `SharedPtr<T>` on the interface:

```csharp
public partial struct WorldRegionRef : IEntityComponent
{
    public SharedPtr<IReadOnlyWorldRegion> Value;
}
```

The seeder calls `SharedPtr.Alloc(world, blobId, concrete)` with the mutable instance — entity-side reads only see the read-only face.

### Safe property types

Public properties on an `[Immutable]` type must return types from the "obviously immutable" set (TRECS126):

- Primitives, `string`, enums
- `readonly struct`s and other `[Immutable]` types (recursively)
- BCL read-only views — `ImmutableArray<T>`, `ImmutableList<T>`, `ReadOnlyMemory<T>`, `ReadOnlySpan<T>`, `ReadOnlyCollection<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyDictionary<TKey, TValue>`, `IReadOnlySet<T>`
- Unity native read-only views — `NativeArray<T>.ReadOnly`, `NativeHashMap<TKey, TValue>.ReadOnly`, `NativeHashSet<T>.ReadOnly`, `NativeParallelHashMap<TKey, TValue>.ReadOnly`, `NativeParallelMultiHashMap<TKey, TValue>.ReadOnly`

Method return types on `[Immutable]` interfaces that aren't in the safe set trigger TRECS127 as a warning. Annotate with `[Trecs.AllowMutableReturn]` when intentional:

```csharp
[Trecs.Immutable]
public interface IReadOnlyWorldRegion
{
    int CellCount { get; }

    [Trecs.AllowMutableReturn]
    Dictionary<int, List<short>> GetCellLookup();
}
```

### Limits

Two cases `[Immutable]` can't enforce:

- **Aliasing.** If the caller keeps a reference to a mutable collection passed into the constructor, they can mutate the blob after construction. Copy inputs or use immutable collection types.
- **Downcasting.** `(WorldRegion)readOnlyView.Get(world)` recovers the mutable surface. Make the concrete `internal` if this matters.

## Pattern A — clone from a provider

For a small, fixed set of shared assets, expose the seeder's handles through a typed provider:

```csharp
public class PaletteProvider
{
    SharedPtr<ColorPalette> _warm;
    SharedPtr<ColorPalette> _cool;

    public void Initialize(WorldAccessor world)
    {
        _warm = SharedPtr.Alloc(world, PaletteIds.Warm, BuildWarm());
        _cool = SharedPtr.Alloc(world, PaletteIds.Cool, BuildCool());
    }

    public SharedPtr<ColorPalette> NewWarmHandle(WorldAccessor world) => _warm.Clone(world);
    public SharedPtr<ColorPalette> NewCoolHandle(WorldAccessor world) => _cool.Clone(world);

    public void Dispose(WorldAccessor world)
    {
        _warm.Dispose(world);
        _cool.Dispose(world);
    }
}
```

Spawners depend on `PaletteProvider`, not on `BlobId` constants:

```csharp
world.AddEntity<MyTag>()
    .Set(new PaletteRef { Value = palettes.NewWarmHandle(world) });
```

Type-safe, discoverable in IntelliSense, no ID registry to maintain.

## Pattern B — look up by stable `BlobId`

Any code with the `BlobId` can resolve the blob without a reference to the provider:

```csharp
// Seeder (BlobPtr.Alloc pins the blob in the cache)
_warm = BlobPtr.Alloc(blobCache, PaletteIds.Warm, BuildWarm());

// Spawner — no provider injected. Acquire looks up by ID.
world.AddEntity<MyTag>()
    .Set(new PaletteRef
    {
        Value = SharedPtr.Acquire<ColorPalette>(world, PaletteIds.Warm),
    });

// Safe variant — returns false if the blob doesn't exist yet.
if (SharedPtr.TryGet<ColorPalette>(world, PaletteIds.Warm, out var ptr))
{
    // use ptr
}
```

`Acquire` finds the existing blob, bumps its refcount, and returns a handle. `TryGet` does the same but returns `false` instead of throwing when the blob is missing. Both also exist on `NativeSharedPtr`.

This pattern is *necessary* in two cases:

- **Content-pipeline assets** where IDs are assigned by an importer and baked into level data.
- **Snapshot reload.** Snapshots serialize the `BlobId`; on reload the heap must contain a blob under that ID for the pointer to resolve.

## Choosing `BlobId` values

`BlobId` wraps a 64-bit `long`. Put the value behind a named constant so call sites read `PaletteIds.Warm`:

```csharp
public static readonly BlobId Warm = new(/* ??? */);
```

Options:

- **Random 64-bit literals** — `new(0x7f3a9b21d4e6c5a8)`. Generate once, paste in, never change. Zero collision risk across modules. A reasonable default.
- **Domain keys** — `BlobIdGenerator.FromKey(42)`. Wraps a `long` with a zero-check guard. Convenient when you already have a numeric domain identifier.
- **Stable string hashes** — `BlobIdGenerator.FromBytes(Encoding.UTF8.GetBytes("warm-palette"))`. Derivable from the name; useful when IDs round-trip through text formats.
- **Asset-pipeline IDs** — GUIDs or content hashes from an importer, cast or hashed to `long`.
- **Hand-assigned small ints** — `new(1001)`, `new(1002)`, etc. Simplest for a single codebase, but risks collision in multi-module setups.
