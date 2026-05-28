# Experimental

The features under this section are part of Trecs but their **API surface is less stable**. Their shapes, names, and entry points may shift as we iterate on them.

## What's here

- **[Pointers](pointers.md)** — All four persistent pointer types (`SharedPtr` / `UniquePtr` / `NativeSharedPtr` / `NativeUniquePtr`) plus their four `Input*` counterparts for `[Input]` components.
- **[Shared Heap Data](shared-heap-data.md)** — Seeder patterns and `BlobId` strategies for blobs shared across many entities.
- **[BlobBuilder](blob-builder.md)** — Relocatable-blob authoring for variable-sized native shared blobs that exceed the inline-storage caps.
- **[Dynamic Collections](dynamic-collections.md)** — Growable unmanaged per-entity collections (`TrecsList<T>`, `TrecsArray<T>`, `TrecsDictionary<TKey, TValue>`) that round-trip through snapshots automatically.
- **[Serialization](serialization.md)** — `ISerializer<T>` and `IComponentArraySerializer<T>` for managed payloads on the heap and for overriding how a component's array round-trips through snapshots, recordings, and checksums.
