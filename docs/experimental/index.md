# Experimental

The features under this section are part of Trecs but their **API surface is still in flux**. They work — most are exercised in production code — but their shapes, names, and entry points may shift between 0.x releases as we iterate on them.

You can use these features in real projects; just expect to touch call sites when you upgrade Trecs versions. If you hit a rough edge, [file an issue](https://github.com/svermeulen/trecs/issues) — feedback on these surfaces is exactly what we need before promoting them to stable.

Everything outside this section is considered stable for 0.x: we still reserve the right to make breaking changes ahead of 1.0, but those changes will be deliberate, called out in the changelog, and migration paths will be provided.

## What's here

- **[Pointers](pointers.md)** — All four persistent pointer types (`SharedPtr` / `UniquePtr` / `NativeSharedPtr` / `NativeUniquePtr`) plus their four `Input*` counterparts for `[Input]` components.
- **[Shared Heap Data](shared-heap-data.md)** — Seeder patterns and `BlobId` strategies for blobs shared across many entities.
- **[BlobBuilder](blob-builder.md)** — Relocatable-blob authoring for variable-sized native shared blobs that exceed the inline-storage caps.
- **[Trecs Collections](trecs-collections.md)** — Growable unmanaged per-entity collections (currently `TrecsList<T>`) that round-trip through snapshots automatically.
- **[Serialization](serialization.md)** — `ISerializer<T>` and `IComponentArraySerializer<T>` for managed payloads on the heap and for overriding how a component's array round-trips through snapshots, recordings, and checksums.
