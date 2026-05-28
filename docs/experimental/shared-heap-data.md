# Shared Heap Data

!!! warning "Experimental"
    The `SharedPtr` / `BlobPtr` / `BlobId` surface this page describes is experimental and may change in future 0.x releases.

Some heap data — palettes, loot tables, animation curves, baked navmeshes — is referenced by many entities and needs to live once, shared, and be released when the last reference disappears. This article covers the patterns for managing that data: how to keep a shared blob alive, how spawners reference it, and how stable identity interacts with snapshots and replay.

For the underlying pointer mechanics (`SharedPtr<T>`, `Clone`, `Dispose`), see [Pointers](pointers.md).

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

A *seeder* is a long-lived object that allocates each blob once at startup and holds a pinning handle as a member. Without that anchor, the cache would evict the blob between init and the first entity spawn, and the next lookup would fail.

The anchor can be either a **`BlobPtr<T>`** (cache-layer pin) or a **`SharedPtr<T>`** (ECS-layer refcounted handle). Both keep the blob resident; they just live at different layers:

- **`BlobPtr<T>` — cache-layer pin.** The lower-level pinning handle; sits in the `BlobCache` and doesn't participate in the ECS refcount that `SharedPtr<T>` adds on top. Use this when the seeder's only job is to anchor data — it makes the seeder independent of `World`/`WorldAccessor` (takes a `BlobCache` directly) and signals that the anchor is *not* an ECS participant. Best fit for the simple "pin at startup, dispose at shutdown" shape.
- **`SharedPtr<T>` — ECS-layer refcount.** Bumps the same refcount entity-side handles use. Use this when the seeder doubles as a *provider* that hands out clones to spawners ([Pattern A below](#pattern-a-clone-from-a-provider)) — having the provider live in the same pointer type it hands out keeps `Clone` natural.

A plain `BlobPtr` seeder:

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

The equivalent `SharedPtr` shape — used in [Pattern A](#pattern-a-clone-from-a-provider) below, where the seeder also hands clones out:

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

Either is enough to keep the blobs alive. The next question is how entity spawners get their own handles to these blobs. There are two patterns.

## Two adoption paths for `[Immutable]`

`SharedPtr<T>` requires `T` to carry `[Trecs.Immutable]` (or be on the implicit built-in allowlist — `string`, `Type`, etc.). Without it, the cache layer can't trust that blob bytes are stable for the lifetime of the blob, and any post-`Alloc` mutation silently desyncs determinism since the `BlobCache` is not snapshotted with game state. The marker is opt-in, and it can apply to two different things — pick whichever fits the type:

- **Class route — `[Immutable] sealed class Foo`.** The class itself is structurally audited by TRECS126: every instance field `readonly`, no public setters, public field/property types are in the "obviously immutable" set, and any non-`object` base class also carries `[Immutable]`. Best fit for **small leaf types built once via a constructor that takes everything** — colour palettes, content descriptors, baked lookup tables. `ColorPalette` from [Sample 14 — Blob Seed Pattern](../samples/14-blob-seed-pattern.md) is the canonical example.
- **Interface route — `[Immutable] interface IReadOnlyFoo { ... }`.** Declare a read-only interface, mark *it* `[Immutable]`, and parameterize the `SharedPtr<T>` on the interface; the (mutable) concrete behind the interface implements the read members. Best fit for **fat retrofit-heavy types whose existing construction lifecycle is incompatible with field-level immutability** — pool-allocated, deserialized in place, populated by a multi-pass builder. The concrete keeps its mutable fields, its Pool, its `ISerializer<T>`; the analyzer only validates the interface's surface. The audit is smaller than for classes — interfaces have no fields and no base — so it only checks: no settable property accessors (`init` is allowed), no events, public property types in the same "safe" set as for classes. Method-return immutability is enforced as a *warning* by TRECS127 (see below).

The lever for the choice is whether the type's construction model can be reshaped around a single constructor call. Greenfield leaf data usually can; pool-managed runtime objects usually can't, and rewriting them just to fit `SharedPtr` is a poor trade.

### Interface route — worked example

The shape that lets you adopt `SharedPtr` without touching an existing class's construction model:

```csharp
// Read-only face — what SharedPtr<T> hands out.
[Trecs.Immutable]
public interface IReadOnlyWorldRegion
{
    int RegionId { get; }
    float Radius { get; }
    IReadOnlyList<Vector3> Waypoints { get; }
    IReadOnlyList<IReadOnlyPortal> Portals { get; }   // covariance — see below
}

// Per-portal read-only face. The concrete Portal class can keep its
// mutable Pool+Serializer construction lifecycle; only this interface
// crosses the SharedPtr boundary.
[Trecs.Immutable]
public interface IReadOnlyPortal
{
    Vector3 Position { get; }
    int LinkedRegionId { get; }
}

// Mutable concrete — keeps its existing pool / serializer / deserialize-
// in-place lifecycle. Not [Immutable] itself. Implements the read face
// via explicit-interface forwarders so external callers don't see the
// mutable surface unless they downcast.
public sealed class WorldRegion : IReadOnlyWorldRegion
{
    public int RegionId;          // mutable; populated by deserializer
    public float Radius;
    public List<Vector3> Waypoints = new();
    public List<Portal> Portals = new();

    int IReadOnlyWorldRegion.RegionId => RegionId;
    float IReadOnlyWorldRegion.Radius => Radius;
    IReadOnlyList<Vector3> IReadOnlyWorldRegion.Waypoints => Waypoints;

    // Covariance trick: List<Portal> is invariant in T (List<Portal>
    // cannot become List<IReadOnlyPortal>), but IReadOnlyList<out T> is
    // covariant — Portal : IReadOnlyPortal lets us hand out an
    // IReadOnlyList<IReadOnlyPortal> view of the same backing storage
    // with no copy and no per-frame allocation. Most retrofit codebases
    // miss this and end up wrapping each call in a Select+ToList.
    IReadOnlyList<IReadOnlyPortal> IReadOnlyWorldRegion.Portals => Portals;
}
```

Spawners then parameterize `SharedPtr<T>` on the interface:

```csharp
public partial struct WorldRegionRef : IEntityComponent
{
    public SharedPtr<IReadOnlyWorldRegion> Value;
}
```

The seeder still calls `SharedPtr.Alloc(world, blobId, concrete)` with the mutable `WorldRegion` instance — the heap stores it boxed as the interface, and entity-side reads only see the read-only face. The concrete's existing Pool / `ISerializer<WorldRegion>` keep working unchanged.

#### `IReadOnlyList<out T>` covariance — the one trick most readers won't know

`List<MutableT>` is invariant in `T`, so `List<MyMutable>` does not implicitly convert to `IReadOnlyList<IReadOnlyT>`. `IReadOnlyList<out T>` *is* covariant (the `out` in the BCL declaration), so if `MyMutable : IReadOnlyT`, the upcast is free:

```csharp
List<MyMutable> _list = new();
// Direct field exposes mutation — bad.
public List<MyMutable> ListMut => _list;
// Read-only view — covariant upcast, no allocation, no copy.
public IReadOnlyList<IReadOnlyT> ListView => _list;
```

This is the cleanest way to project a list of mutable concretes through an `[Immutable]` interface as a list of read-only faces without a per-call allocation. The same trick applies to `IEnumerable<out T>` but not to `IDictionary<TKey, TValue>` or `List<T>` (both invariant).

#### Safe property types — what the analyzer trusts

Public properties on an `[Immutable]` interface must return types from the same "obviously immutable" set TRECS126 enforces for class fields:

- primitives, `string`, enums;
- `readonly struct`s (recursively) and other `[Immutable]` types (recursively);
- BCL read-only views — `ImmutableArray<T>`, `ImmutableList<T>`, `ReadOnlyMemory<T>`, `ReadOnlySpan<T>`, `ReadOnlyCollection<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyDictionary<TKey, TValue>`, `IReadOnlySet<T>` (where the target framework supports it);
- Unity native read-only views — `NativeArray<T>.ReadOnly`, `NativeHashMap<TKey, TValue>.ReadOnly`, `NativeHashSet<T>.ReadOnly`, `NativeParallelHashMap<TKey, TValue>.ReadOnly`, `NativeParallelMultiHashMap<TKey, TValue>.ReadOnly`.

### Method-return immutability — TRECS127

Method-return types on `[Immutable]` interfaces are validated as a *warning* by TRECS127: a method whose return type isn't in the safe set above fires unless the method is annotated with `[Trecs.AllowMutableReturn]`. The exemption exists because interface methods can legitimately mean "live alias", "fresh defensive copy", "computed transformation", or "subset view" — the analyzer cannot tell which from a signature alone — but surfacing the choice as a warning makes the reviewer look.

Apply `[AllowMutableReturn]` at the method declaration site when the looseness is intentional. Add a comment above the attribute when reviewer-facing rationale is useful:

```csharp
[Trecs.Immutable]
public interface IReadOnlyWorldRegion
{
    // Safe return — no annotation.
    int CellCount { get; }

    // Escape: returns a mutable concrete. Callers can mutate the shared
    // blob through this reference. Dict is shared-mutable by convention;
    // presenter callers only read.
    [Trecs.AllowMutableReturn]
    Dictionary<int, List<short>> GetCellLookup();
}
```

`void` methods and non-ordinary methods (operators, property accessors, etc.) are not checked. The attribute is method-level only and not inherited — each override / re-declaration must opt in itself so the escape stays explicit at every declaration site.

### Limits the type system can't reach

Two cases the marker can't enforce, regardless of which adoption path you chose:

- **Aliasing across the constructor boundary.** If the caller keeps a reference to a mutable collection passed into the constructor (class route) or the concrete (interface route), they can mutate the shared blob after construction. Take a `ReadOnlySpan<T>` / `IReadOnlyList<T>` and copy, or use an immutable collection type for the parameter.
- **Downcast through an `[Immutable]` interface to the mutable concrete.** `(WorldRegion)readOnlyView.Get(world)` recovers the mutable surface. The interface route trusts the convention "don't downcast"; if it matters, make the concrete `internal` to the assembly that owns construction, or `private` inside a containing factory.

The marker is a determinism guardrail, not a sandbox. Code review still catches the rest.

## Pattern A — clone from a provider

If the shared assets are a small, fixed, named set, expose the seeder's handles through a typed provider and have spawners `Clone` them:

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

This is the most idiomatic shape for a fixed set: type-safe, discoverable in IntelliSense, no integer identifiers leaking into spawn code, no manual ID registry to maintain. `Clone` bumps the refcount the same way an ID lookup would, so the runtime behaviour is identical.

## Pattern B — look up by stable `BlobId`

If you give each blob an explicit `BlobId` when seeding, any code can resolve it later without holding a reference to the provider:

```csharp
// Seeder (BlobPtr.Alloc pins the blob in the cache)
_warm = BlobPtr.Alloc(blobCache, PaletteIds.Warm, BuildWarm());

// Spawner — no provider injected. Acquire looks up by ID.
world.AddEntity<MyTag>()
    .Set(new PaletteRef
    {
        Value = SharedPtr.Acquire<ColorPalette>(world, PaletteIds.Warm),
    });
```

`SharedPtr.Acquire<T>(world, blobId)` finds the existing blob, bumps its refcount, and returns a fresh handle. It's `Clone` addressed by ID instead of by reference. The seeder's `BlobPtr` and each entity's `SharedPtr` pin the same underlying cache entry through independent mechanisms — the blob stays resident as long as either side holds at least one handle. (A `SharedPtr` seeder works here too; using `BlobPtr` just makes "anchor, not consumer" explicit.)

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
- **Stable string hashes** — `BlobIdGenerator.FromBytes(Encoding.UTF8.GetBytes("warm-palette"))`. Same collision profile as random literals at 64 bits, with the bonus that the value is derivable from the name. Useful when IDs need to round-trip through text (config files, save formats) or when you want the source of truth to be the string rather than the literal.
- **Asset-pipeline IDs** — GUIDs or content hashes the importer already produced, cast or hashed down to `long`. The right answer when the blob originates from a content pipeline; the `BlobId` design is built for this case.
- **Hand-assigned small ints** — `new(1001)`, `new(1002)`, … Simplest for a single registry in a single codebase. The drawback is brittleness in multi-module setups: if two independent codebases both start their registries at `1001`, they collide on shared blob stores. Fine if you control all the code that mints stable IDs; reach for one of the wider-range options if you don't.
