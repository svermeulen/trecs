# 19 — Shared Interface

The **interface adoption path** for `SharedPtr<T>`: a mutable concrete class keeps its existing construction lifecycle and is exposed to entity-side callers through an `[Immutable]` read-only interface.

**Source:** `com.trecs.core/Samples~/Tutorials/19_SharedInterface/`

## What it does

Two clusters of cubes — eight to the north, eight to the south — each scaled and tinted from a shared `WorldRegion` blob via its read-only interface face. The north cluster cycles cool tones at radius 4; the south cluster cycles warm tones at radius 2.5. Every dweller in a cluster holds its own 12-byte `SharedPtr<IReadOnlyWorldRegion>` handle to the same underlying blob.

## The two adoption paths

`SharedPtr<T>` accepts either of two adoption paths for `T`:

| | Class route (Sample 15) | **Interface route (this sample)** |
|---|---|---|
| `[Immutable]` on | the class itself | a read-only interface |
| Audit | structural — every field `readonly`, no public setters, safe field types, immutable base class | interface surface — no settable accessors, no events, safe public property types |
| Concrete is | the class as written (must be structurally immutable) | a separate mutable class implementing the interface |
| Construction | constructor-takes-everything | unchanged — pool / serializer / multi-pass builder, whatever already exists |
| Best fit | small leaf types, content descriptors, baked lookup tables | retrofit-heavy types whose existing lifecycle is incompatible with field-level immutability |

The lever is whether the type's construction model can be reshaped around a single constructor call. Greenfield leaf data usually can; pool-managed runtime objects usually can't, and rewriting them just to fit `SharedPtr` is the wrong trade. See [Shared Heap Data — Two adoption paths for `[Immutable]`](../experimental/shared-heap-data.md#two-adoption-paths-for-immutable) for the full breakdown.

## Schema

The read-only interface is the analyzer-facing surface:

```csharp
[Immutable]
public interface IReadOnlyWorldRegion
{
    string Name { get; }
    float Radius { get; }
    IReadOnlyList<Color> Palette { get; }

    // See "The TRECS127 opt-out" below.
    [AllowMutableReturn]
    List<Color> GetPaletteMutable();
}
```

The mutable concrete is *not* `[Immutable]`. It keeps its retrofit-friendly construction shape (public mutable fields, populate-then-pass-to-Alloc) and forwards the read API through explicit-interface members:

```csharp
public sealed class WorldRegion : IReadOnlyWorldRegion
{
    public string Name;
    public float Radius;
    public List<Color> Palette = new();

    string IReadOnlyWorldRegion.Name => Name;
    float IReadOnlyWorldRegion.Radius => Radius;
    IReadOnlyList<Color> IReadOnlyWorldRegion.Palette => Palette;
    List<Color> IReadOnlyWorldRegion.GetPaletteMutable() => Palette;
}
```

The entity-side component holds the handle parameterised on the **interface**:

```csharp
public partial struct RegionRef : IEntityComponent
{
    public SharedPtr<IReadOnlyWorldRegion> Value;
}
```

## Seeding

The seeder allocates each blob with `SharedPtr.Alloc<TInterface>(heap, blobId, concreteInstance)` — `T` is the interface, the value is the mutable concrete. The analyzer's TRECS125 check accepts the call because `IReadOnlyWorldRegion` is marked `[Immutable]`; everything that flows out of the heap from this point is typed as the interface.

```csharp
_north = SharedPtr.Alloc<IReadOnlyWorldRegion>(world.Heap, RegionIds.North, BuildNorth());
_south = SharedPtr.Alloc<IReadOnlyWorldRegion>(world.Heap, RegionIds.South, BuildSouth());

static WorldRegion BuildNorth() => new()
{
    Name = "North",
    Radius = 4f,
    Palette = { /* cool tones */ },
};
```

Construction is the existing shape — assign fields after `new`, no constructor-takes-everything required. That's the whole point: this is what makes the interface route a retrofit path.

## Entity-side lookup

Dwellers acquire fresh handles by stable `BlobId` — same shape as Sample 15, just parameterised on the interface:

```csharp
world.AddEntity<SampleTags.Dweller>()
    .Set(new RegionRef
    {
        Value = SharedPtr.Acquire<IReadOnlyWorldRegion>(world.Heap, regionId),
    })
    .AssertComplete();
```

## Reading the blob from a system

Systems dereference the handle through the heap accessor and only ever see the read-only face:

```csharp
public partial class RegionAppearanceSystem : ISystem
{
    [ForEachEntity(typeof(SampleTags.Dweller))]
    void Execute(in RegionRef region, ref UniformScale scale, ref ColorComponent color)
    {
        IReadOnlyWorldRegion view = region.Value.Get(World.Heap);

        scale.Value = view.Radius * 0.5f;
        // ... cycle color through view.Palette
    }
}
```

Reaching the mutable surface from here requires an explicit `(WorldRegion)view` downcast — the documented escape hatch the type system can't prevent. Make the concrete `internal` to the assembly that owns construction if it matters.

## The TRECS127 opt-out

`IReadOnlyWorldRegion` deliberately exposes one method that returns the concrete's mutable `List<Color>` by alias:

```csharp
// Dict is shared-mutable by convention; ...
[AllowMutableReturn]
List<Color> GetPaletteMutable();
```

Without the `[AllowMutableReturn]` annotation, **TRECS127** would fire — the analyzer warns whenever a method on an `[Immutable]` interface returns a type that isn't provably immutable per the same safe-type walker TRECS126 uses for fields and property types. The attribute is the explicit, declaration-local opt-out, so the looseness is visible at the interface file rather than implicit in the concrete's impl. Add a comment above the attribute when reviewer-facing rationale is useful.

`void` methods and non-ordinary methods (operators, property accessors, etc.) are not checked. The attribute is method-level only and not inherited — each override / re-declaration must opt in itself.

Property types on the interface (`Name`, `Radius`, `Palette`) don't need any annotation — they're already in the safe set: `string`, primitive, and `IReadOnlyList<T>` respectively. See [Shared Heap Data — Safe property types](../experimental/shared-heap-data.md#safe-property-types-what-the-analyzer-trusts) for the full list, which also includes the BCL read-only views (`ImmutableArray<T>`, `ReadOnlyMemory<T>`, etc.) and the Unity native read-only views (`NativeArray<T>.ReadOnly`, `NativeHashMap<K,V>.ReadOnly`, etc.).

## Cleanup discipline

Same as Sample 15: the seeder anchors live for the lifetime of the seeder; entity-owned handles aren't disposed in this sample because no entities are removed during play. For entities that come and go, register an `OnRemoved` observer to dispose each entity's handle — see [Sample 10 — Pointers](10-pointers.md).

## When to reach for this

- An existing class whose construction model can't be reshaped — pool-allocated, deserialized in place, populated by a multi-pass builder.
- A class you don't own (third-party library / older subsystem) that you want to ferry through `SharedPtr` without forking.
- Any case where the read API is naturally narrower than the write API — the interface lets you publish only the read half.

For new leaf types built once via a constructor (palettes, lookup tables, content descriptors), prefer the class route — [Sample 15 — Blob Seed Pattern](15-blob-seed-pattern.md). For unmanaged shared blobs read by Burst jobs, see [Sample 18 — Heightmap Blobs](18-heightmap-blobs.md).

## Concepts introduced

- **Interface adoption path** — `[Immutable]` on a read-only interface; the mutable concrete behind it stays as-is
- **`SharedPtr.Alloc<TInterface>(heap, id, concreteInstance)`** — parameterise the handle on the interface, pass the concrete as the value
- **TRECS127** — analyzer warning when an `[Immutable]` interface method returns a non-safe type; documents method-return immutability without enforcing it everywhere
- **`[AllowMutableReturn]`** — the explicit, declaration-local opt-out for TRECS127; add a comment above the attribute when reviewer-facing rationale is useful
- **Safe property types on `[Immutable]` interfaces** — primitives, `string`, enums, `readonly struct`s, other `[Immutable]` types, BCL read-only views, Unity native read-only views, and Trecs's `DenseDictionary` / `DenseHashSet` via their `IReadOnlyDictionary` / `IReadOnlyCollection` projections
- **`IReadOnlyList<out T>` covariance** — project `List<MutableT>` as `IReadOnlyList<IReadOnlyT>` without copies or per-call allocations; see [Shared Heap Data](../experimental/shared-heap-data.md#ireadonlylistout-t-covariance-the-one-trick-most-readers-wont-know)
