# Debugging & Troubleshooting

A runtime with deferred structural changes, source-generated systems, and Burst-compiled jobs has a few common tripping points. This page collects the patterns for diagnosing them.

## Inspecting entity state at runtime

A `WorldAccessor` is your read/write view of the world. To inspect an entity by hand (e.g. from an editor tool or a debugger-triggered logger):

```csharp
// You already have a WorldAccessor from a system, or from world.CreateAccessor(AccessorRole.X).
var entity = world.Query().WithTags<PlayerTag>().Single();
var health = entity.Get<Health>().Read.Value;
UnityEngine.Debug.Log($"Player health: {health}");
```

To iterate every entity in a group, use [`GroupSlices`](../data-access/queries-and-iteration.md) and index the read/write buffer. A plain `foreach` over tags works for one-off inspection too.

To ask "does this specific entity still exist?", use `EntityHandle.Exists(world)`. Handles can outlive the entity they reference (generational IDs), so always check before calling `.Get<T>()`.

## Looking at generated source

When a source generator emits code, the output lands in your project's `obj/<Configuration>/<TargetFramework>/generated/` folder (Rider, VS, and VS Code all expose these files in the Solution Explorer under **Dependencies → Analyzers**). Open the generated file to see exactly what the source generator produced for a given `ISystem` / `ITemplate` / aspect.

If the generator fails, you'll see a `TRECS###` diagnostic on the type it was trying to generate for. The diagnostic ID doubles as a stable anchor for searching in the repo.

To enable source-generator timing logs during local debugging, add `SOURCEGEN_TIMING` to the generator's `DefineConstants` in `SourceGen/Trecs.SourceGen/Trecs.SourceGen/Trecs.SourceGen.csproj`. The timings fire to `SourceGenLogger.Log`, which writes to the Unity console.

## "No one ever sees my new entity"

Structural changes — `AddEntity`, `RemoveEntity`, `MoveTo` — are deferred until submission. If you spawn an entity inside a fixed-update system, other systems in the *same* fixed tick won't see it unless they're explicitly ordered after a submission boundary.

If you want new entities to be visible immediately, call `world.SubmitEntities()` manually after the mutation — but prefer letting the pipeline handle it. Structure your systems so that readers run after writers.

## "`HasAnyCriteria` failed: Query has no criteria"

Raised by `QueryBuilder` terminators when called with no filters applied. Always chain at least one of `.WithTags<T>()`, `.WithComponents<T>()`, `.WithoutTags<T>()`, or `.WithoutComponents<T>()` before terminating with `.EntityIndices()`, `.GroupSlices()`, `.Groups()`, `.Count()`, or `.Single()`.

## "Found N native blob handles that were not disposed"

Logged by `NativeSharedHeap` / `NativeUniqueHeap` on world disposal. You allocated a `NativeSharedPtr` / `NativeUniquePtr` and left a reference alive when the world shut down. The usual culprit is forgetting to dispose in an entity-removal observer:

```csharp
world.Events.EntitiesWithTags<MyTag>()
    .OnRemoved(OnRemoved)
    .AddTo(_disposables);

[ForEachEntity]
void OnRemoved(in MyBlobRef blobRef)
{
    blobRef.Value.Dispose(world);
}
```

See [Sample 10: Pointers](../samples/10-pointers.md) and [Sample 14: Native Pointers](../samples/14-native-pointers.md).

## "NativeSharedPtrResolver could not resolve blob ... created this frame and not yet flushed"

You created a blob via `CreateBlob` on the main thread and then tried to resolve it inside a Burst job before the submission pipeline flushed pending adds into `_allEntries`. Either:

- Let the default pipeline run (the submission boundary flushes pending ops before scheduling its own jobs), or
- Check `heap.PendingAddCount` and call `heap.FlushPendingOperations()` yourself if you're scheduling custom jobs that consume freshly-created blobs.

## Desync during recording playback

When recording is active, `BundleRecorder` checksums world state every N fixed frames (`BundleRecorderSettings.ChecksumFrameInterval`). During playback, `BundlePlayer.Tick` recomputes the checksum at the same frames and compares. A mismatch surfaces as `BundlePlaybackState.Desynced`, with `HasDesynced == true` and `DesyncedFrame` set to the first failing frame.

Common causes:

- **Non-deterministic state in a component.** For example: a `float Age` field driven off `UnityEngine.Time.deltaTime` instead of `WorldAccessor.FixedDeltaTime`. Use `World.Rng` and `world.FixedDeltaTime` exclusively inside fixed-update systems.
- **Managed reference in a component.** Components must be unmanaged. A string or array will compare by reference equality between runs and checksum differently.
- **Mutable state on a fixed-update system.** Systems are not serialized — state kept on a system field diverges between recording and playback.
- **`UnityEngine.Random` anywhere in the fixed-update path.** Replace with `World.Rng`.

When checksums diverge, the [Determinism](./design-rules.md#determinism) checklist is the first stop.

## Post-deserialization checksum mismatch

If `BundlePlayer.Start` throws `SerializationException` because the post-deserialization world's checksum disagrees with `RecordingBundle.InitialSnapshotChecksum`, that points at a **serialization defect** (not a simulation desync). Look for:

- A custom `ISerializer<T>` that doesn't round-trip byte-identically.
- A serializer that writes different bytes depending on `writer.HasFlag(...)` without the same flags being set on load.
- A `DenseDictionary`/`DenseHashSet` that's being mutated during iteration while serializing.

## Burst compilation failures

The most common causes:

- **Virtual calls / interface dispatch.** Inline the logic or refactor to static methods.
- **Managed types.** Strings, delegates (except `FunctionPointer<T>`), and `IDisposable` patterns that box are all out. Use Unity's `FixedString*` types where a string-like thing is needed.
- **Exceptions in a hot path.** Burst strips exception throws; the path that would have thrown continues with undefined state in release. Assert loudly at the boundary rather than relying on exceptions.

The Burst Inspector (`Jobs → Burst → Open Inspector`) shows per-method compilation status and the failure reason.

## When in doubt: check the diagnostic

Every source-generator failure emits a `TRECS###` diagnostic with a specific message. The ID is stable (tracked in `SourceGen/Trecs.SourceGen/Trecs.SourceGen/AnalyzerReleases.Shipped.md`), so you can search for the ID in both your code and this repo to find exactly what triggered it and why.
