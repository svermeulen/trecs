# Heap Allocation Rules

## The seeder pattern — shared assets across many entities

A single piece of immutable data (a palette, a loot table, an animation curve, a path) is referenced by many entities. The data should exist once, live as long as any references hold it, and be released when the last reference disappears.

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

`AllocShared<T>(BlobId)` (the lookup-only overload) creates a new handle pointing at the existing blob and bumps its reference count. The blob lives as long as the seeder's handle plus any entity-owned handles are non-zero.

### Cleanup is manual

Pointers stored on components must be disposed when the entity is removed — Trecs does **not** auto-dispose. The standard pattern is an `OnRemoved` observer on the relevant tag:

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

See [Sample 10 — Pointers](../samples/10-pointers.md) for a reference implementation.

## Cheat sheet

- **Persistent allocation from init or `Fixed` systems** (and `Unrestricted` if you must). Input systems use `AllocXxxFrameScoped` for transient payloads; `Variable`-cadence systems can't allocate at all.
- **Auto-IDs are deterministic** when init and fixed code is deterministic — same rule as replay.
- **Use explicit `BlobId`s** for stable identity independent of startup ordering — particularly for content-pipeline assets.
- **Seeder pattern**: a long-lived class holds `SharedPtr<T>` members for shared assets, allocated once at init with a stable `BlobId`, looked up by entities via `AllocShared(BlobId)`.
- **Always dispose** pointers stored on components from an `OnRemoved` observer.
- **Presentation systems read; they don't allocate heap, and they only spawn `[VariableUpdateOnly]` templates.** If a presentation system needs new sim-state, populate it from a `[VariableUpdateOnly]` component or a fixed system; if it needs render-only entities, declare the template `[VariableUpdateOnly]` and spawn from there.
