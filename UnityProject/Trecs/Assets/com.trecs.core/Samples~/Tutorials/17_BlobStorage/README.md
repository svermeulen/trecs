# 17 — Blob Storage

Shows how to store large, shared, read-heavy data **outside** entity components
using the blob storage system. Blobs are managed data (anything `: class`) held
in a pluggable `IBlobStore` backend; entities reference them through a
16-byte `BlobPtr<T>` handle. The data lives once in the cache regardless of how
many entities point at it.

**When to reach for this over `SharedPtr<T>`:**
- You want a pluggable storage backend — the `IBlobStore` interface lets you
  back blobs with disk files, asset bundles, or a network source.
- You need asynchronous warm-up (`WarmUp` / `GetLoadingState`) for large assets.
- Blobs are uniquely identified by a stable `BlobId`, useful for content
  pipelines that reference assets by ID across sessions.

For a simpler, memory-only, ref-counted pointer to managed data, use
`SharedPtr<T>` from sample 10 instead.

## What the sample does

- Registers a `BlobStoreInMemory` with the world via `WorldBuilder.AddBlobStore`.
- Creates two `ColorPalette` blobs (managed `class` holding a `List<Color>`).
- Spawns a 6×6 grid of cubes, each referencing one of the two palettes via
  `BlobPtr<ColorPalette>` on its `PaletteRef` component.
- A `PaletteCycleSystem` reads each entity's blob each frame and samples the
  palette over time, driving the cube's colour.

## Key APIs

- `WorldBuilder.AddBlobStore(IBlobStore)` — register a blob backend before
  building the world.
- `BlobStoreInMemory` — ready-made in-memory implementation of `IBlobStore`.
  Implement `IBlobStore` yourself to back blobs with disk / asset bundles /
  network.
- `BlobCache.CreateBlobPtr<T>(T blob)` — store a blob and receive a
  `BlobPtr<T>`. The cache auto-generates a `BlobId`. Use the overload
  `CreateBlobPtr<T>(BlobId, T)` when you want a stable, content-pipeline-driven
  ID.
- `BlobPtr<T>.Get(BlobCache)` — retrieve the managed blob from a handle.
- `BlobPtr<T>.Clone(BlobCache)` — mint a new handle pointing at the same blob;
  each entity holds its own handle so the blob lives until all entities are
  destroyed.
- `BlobPtr<T>.Dispose(BlobCache)` — release a handle when you're done with it.

## A note on BlobCache access

`BlobCache` is currently reached via a `Trecs.Internal` extension method
(`world.GetBlobCache()`). Add `using Trecs.Internal;` to reach it. This is the
only sample that uses `Trecs.Internal` directly — expect this entry point to
become a first-class public API in a future release.

## Setup (manual)

1. Create a new scene. Add a Camera.
2. Add a GameObject with **Bootstrap** and **BlobStorageCompositionRoot**.
   Drag BlobStorageCompositionRoot into Bootstrap's `CompositionRoot` field.
3. Press Play. You should see two interleaved groups of cubes slowly shifting
   through warm and cool palettes on independent cycles.

Documentation: https://svermeulen.github.io/trecs/samples/17-blob-storage/
