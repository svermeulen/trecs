
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.3.0] - Unreleased

### Fixed

- **`Tag<T>.Value` is now safe to read from Burst AOT code (issue #10).** It was a mutable cached static field, which trips Burst error BC1040 under AOT (Standalone / IL2CPP). In-editor Burst JITs lazily and silently falls back to managed code on such errors, so CI stayed green while shipped AOT builds failed. `Tag<T>.Value` is now a thin wrapper over `TypeId<T>.Value` (a `readonly` Burst constant in default mode, a `SharedStatic` in strict mode).
- **Samples install cleanly into a fresh project.** `SampleCycler` no longer depends on project build settings, and sample rendering is headless-safe.
- **Sample / docs link drift.** Corrected off-by-N tutorial companion-doc slugs, the stale "Pointers" naming for sample 10 (now "Dynamic Collections"), a 404 FAQ link, and a broken docs image reference.
- **`package.json` metadata.** Added the missing `license` (MIT) and `repository` fields required by UPM / OpenUPM.

### Changed

- Sample 10 renamed from **Pointers** to **Dynamic Collections** (doc page `samples/10-dynamic-collections`).
- IL2CPP / Standalone player build settings adjusted for the AOT test path.

### Added

- AOT Burst regression coverage: a Standalone (IL2CPP) PlayMode test that reads `Tag<T>.Value` from inside a Burst job, plus a dedicated standalone-build CI job so Burst AOT errors (e.g. BC1040) fail CI.
- CI check that the committed `Trecs.SourceGen.dll` matches a fresh build of the generator source (semantic decompile comparison), preventing a stale generator DLL from shipping.

## [0.2.0] - 2026-05-07

Large redesign pass covering package consolidation, accessor permissions, heap-backed collections, source-gen diagnostics, native pointer internals, and editor tooling. All breaking changes are mechanical text-level migrations unless otherwise noted.

### Added

- **Single-package layout.** `com.trecs.serialization` merged into `com.trecs.core` — the project is now a single UPM package.
- **Accessor roles.** New `AccessorRole` enum (`Fixed`, `Variable`, `Unrestricted`) replaces the previous `SystemPhase` / `IsEditor` parameters. The role drives component read/write rules, structural-change rules, and heap-allocation rules in one place.
- **Heap-backed collection types.** `TrecsList<T>`, `TrecsDictionary<TKey, TValue>`, and `TrecsArray<T>` — deterministic, serializable, heap-allocated collections that can live inside ECS components. Each has separate `Read`/`Write` wrappers with version-checked safety guards that hold in shipping builds.
- **`BlobBuilder` / `BlobArray<T>` / `BlobRef<T>`.** Build relocatable blob allocations (root struct + trailing arrays) that can be handed to `NativeSharedPtr.AllocTakingOwnership`.
- **Input pointer types.** `InputSharedPtr<T>`, `InputUniquePtr<T>`, `InputNativeSharedPtr<T>`, `InputNativeUniquePtr<T>` for frame-scoped input data that participates in the input recording pipeline.
- **`[NonCopyable]` / `[Copyable]` attributes.** Prevent accidental by-value copies of structs (including `IEntityComponent` by default). Source-gen diagnostics TRECS118–120.
- **`[Immutable]` attribute.** Enforces that types stored via `SharedPtr<T>` are immutable (readonly fields, no public setters). Source-gen diagnostics TRECS125–127.
- **Determinism analyzers.** TRECS128/129 flag `Dictionary`/`HashSet` iteration in fixed-update systems; TRECS130 flags non-deterministic APIs (`DateTime.Now`, `System.Random`, `UnityEngine.Random`, etc.) in fixed-update systems.
- **System-control APIs.** `WorldAccessor.SetSystemEnabled(int, EnableChannel, bool)` with multi-channel AND semantics, plus deterministic `WorldAccessor.SetSystemPaused` for replay-safe pauses. `IsSystemEffectivelyEnabled` query for debug UIs.
- **`WorldAccessor.StepFixedFrame`** for single-frame stepping outside the system runner.
- **Trecs Player editor window.** Record / playback / snapshot / scrub / fork / loop UI, with a Saves library for managing recordings and snapshots.
- **Hierarchy editor window.** Persisted expand/collapse, search, identity-based selection, per-entity component inspector with JSON edit.
- **`WorldRegistry`** — static registry of active `World` instances. `World.DebugName` / `WorldBuilder.SetDebugName(string)` for editor dropdowns.
- **`World.Events.OnShutdown()`** — fires during `World.Dispose()` after `RemoveAllEntities` but before infrastructure teardown.
- **Constructor-positional shorthand on tag attributes.** `[ForEachEntity]`, `[SingleEntity]`, and `[FromWorld]` accept tags as `params Type[]` — e.g. `[ForEachEntity(typeof(EcsTags.Enemy))]`.
- **`[SingleEntity]` is now per-parameter / per-field** and works in plain `Execute`, mixed with `[ForEachEntity]`, `[WrapAsJob]` auto-generated jobs, and hand-written job-struct fields.
- **`[GlobalIndex]` parameter attribute** on iteration `Execute` methods — receives the packed 0..N-1 index across all groups. Job-side only.
- **`[Serializable]` auto-emitted on `IEntityComponent` partials.**, for use with Hierarchy editor window to allow changing entities dynamically at runtime via unity inspector
- **Recording system rewritten as `RecordingBundle`.** New surface: `BundleRecorder`, `BundlePlayer`, `RecordingBundle`, `RecordingBundleSerializer`. A bundle is a single self-contained replayable session: initial snapshot + input queue + sparse desync checksums + auto-anchor snapshots + user snapshots.
- **`EntityHandle.TryToEntity(WorldAccessor)`** overload.
- **Source-gen diagnostics.** ~80 structured diagnostics (TRECS001–TRECS130) with dedicated tests.
- **New samples.** `10_DynamicCollections` (renamed from `10_Pointers`), `13_AspectInterfaces`, `14_BlobSeedPattern`, `15_ReactiveEvents`, `16_MultipleWorlds`, `17_HeightmapBlobs`. `11_Snake` moved from `com.trecs.serialization` into the core tutorials.

### Changed (breaking)

- **`com.trecs.serialization` merged into `com.trecs.core`.** The project is now a single package. Drop `com.trecs.serialization` from your `Packages/manifest.json`.
- **`HeapAccessor` removed as separate class**, folded into `WorldAccessor`. `world.Heap.AllocShared(...)` becomes `world.AllocShared(...)` directly.
- **`EntityAccessor` ref struct removed.** Operations moved to extension methods on `EntityHandle`/`EntityIndex` — e.g. `entity.Component<T>(world)`, `entity.SetTag<T>(world)`.
- **`NativeSharedPtr<T>` rearchitected.** Struct shrinks from 12B to 4B (chunked directory replaces hash-map resolver, enabling concurrent Burst-visible allocation). API: `DisposeHandle` → `DecrementRef`, `ptr.BlobId` → `ptr.GetBlobId(world)`.
- **`[ExecutesAfter]` / `[ExecutesBefore]` renamed to `[ExecuteAfter]` / `[ExecuteBefore]`.**
- **`[Phase(...)]` renamed to `[ExecuteIn(...)]`.**
- **`SetDef` renamed to `EntitySet`** (internal storage to `EntitySetStorage`).
- **Accessor creation API.** `World.CreateAccessor(string)` removed. Use `World.CreateAccessor(AccessorRole, string)`.
- **`[VariableUpdateOnly]` enforced symmetrically.** Writes blocked from non-`Fixed` phases; `[Constant]` writes blocked outside fixed-update. Support on entity sets removed (use templates).
- **`[FixedUpdateOnly]` attribute removed.** Fixed-update systems already have full write permission.
- **Aspect interfaces detected via `IAspect` marker** instead of `[AspectInterface]` attribute.
- **`ITemplate` field declarations must omit access modifiers** (TRECS034 reworked). Generator suppresses CS0169/CS0414/CS0649/IDE0051/IDE0052.
- **`IHasTags<...>` renamed to `ITagged<...>`.**
- **`IHasPartition<...>` replaced by `IPartitionedBy<...>`** with dimension-based semantics. Each declaration is one dimension; multiple declarations cross-product automatically. New arity-1 form `IPartitionedBy<T>` for presence/absence.
- **`MoveTo` removed; `SetTag<T>` / `UnsetTag<T>` are the only structural tag-change verbs.** Multiple calls on the same entity in one submission coalesce; same-dim conflicts throw.
- **`[ForEachEntity(..., Without = typeof(T))]`** queries the absent partition of a presence/absence dimension.
- **Native collection cleanup.** Sequence-shape collections standardize on signed-`int` `Length`. `IsEmpty()` → `IsEmpty` property. `NativeBuffer` collapses read/write pointer accessors into `GetRawPointer`.
- **`FixedArray2/16/128<T>` indexer is read-only.** Writes go through `arr.Mut(i) = value`.
- **`BlobPtr<T>` / `NativeBlobPtr<T>` moved to `Trecs.Internal`.** Use `SharedPtr<T>` / `NativeSharedPtr<T>` via `WorldAccessor`.
- **Heap auto-ID allocation removed.** Variable-update systems can no longer allocate persistent blobs.
- **`SerializationFlags` is now bit-flags** (`long` bitmask, values are powers of two).
- **Fast-forward API targets a frame**, not a time.
- **`SystemRunner.StepFrame` renamed `StepFixedFrame`.**
- **`ISystem.OnReady` runs in execute order** (Input → Fixed → EarlyPresentation → Presentation → LatePresentation), not registration order.
- **`ISystem.OnReady` runs after the global entity is submitted.** `OnAdded` subscriptions from `OnReady` won't fire for global-entity creation.
- **`IComponentAccessRecorder` renamed to `IAccessRecorder`.**
- **`EntityHandle.UniqueId` renamed to `Id`.**
- **`MissingInputBehavior` values shortened:** `ResetToDefault` → `Reset`, `RetainCurrent` → `Retain`.
- **"Bookmark" terminology replaced with "Snapshot"** throughout recording system.
- **Frame-event names made symmetric.** `OnSubmission` → `OnSubmissionCompleted`, `OnPostApplyInputs` → `OnInputsApplied`. New `OnVariableUpdateCompleted` event.
- **`QueryBuilder` method renames:** `Single` → `SingleHandle`, `EntityHandles` → `Handles`, `EntityIndices` → `Indices`.
- **`RemoveAllEntitiesOnDispose` setting removed** — entities are always removed on dispose.
- **`[SingleEntity]` is per-parameter / per-field.** The previous method-level form no longer compiles.
- **`PtrHandle` is now a `readonly struct`.**
- **`NativeArraySerializer<T>` / `NativeListSerializer<T>`** accept an `Allocator` constructor parameter (defaulting to `Persistent`).
- **`IEnumerable` removed from Trecs collection types** to prevent accidental boxing allocations.
- **`10_Pointers` sample renamed to `10_DynamicCollections`.**
- **`13_SaveGame` sample removed.**

### Changed

- **Source-gen polish.** Nested-class scope support, consolidated tag parsing, unified `[SingleEntity]` emit path, value-equatable pipeline models for effective incremental cache, `[GeneratedCode]` attribute stamped on all generated types.
- **System-effectively-enabled** display: hierarchy rows grayed when disabled.
- **Submission pipeline** optimized with Burst-jobified parallel fill, per-group staging bags, native component layout metadata.
- **NativeHeap serialization** optimized with struct blits and direct chunk walk.
- **`BinarySerializationReader`** refactored to operate on `ReadOnlyMemory<byte>` instead of `MemoryStream`.

### Removed

- `com.trecs.serialization` package (merged into `com.trecs.core`).
- `DenseDictionary<TKey,TValue>`, `DenseHashSet<T>`, `NativeDenseDictionary<TKey,TValue>` (replaced by `IterableDictionary` / `IterableHashSet` / `NativeIterableDictionary`).
- `FastList<T>`, `ReadOnlyFastList<T>`, `LocalReadOnlyFastList<T>`.
- `HeapAccessor` class (folded into `WorldAccessor`).
- `EntityAccessor` ref struct.
- `SimpleResizableBuffer<T>`.
- `[FixedUpdateOnly]` attribute.
- `[AspectInterface]` attribute and `AspectValidator.ValidateUsagePatterns`.
- `warnOnMissing` parameter on `[Input(...)]`.
- `RecordingHandler` / `PlaybackHandler` / `AutoRecordingSnapshot` / `PlaybackStartParams` / `RecordingMetadata` (replaced by `BundleRecorder` / `BundlePlayer` / `RecordingBundle`).
- `World.AllocShared` / `World.AllocUnique` (use `WorldAccessor` allocation methods).
- `NativeAllocTracker` (relies on Unity's built-in native-leak detection).
- Legacy `Group.cs` runtime type (`GroupIndex` is the canonical handle).
- `FixedTypeCommon.cs`.
- `SerializableByteArraySerializer` (replaced by `WriteBytes` / `ReadBytes`).
- `IStableHashProvider` (collapsed into `GetHashCode`).
- `SharedNativeInt`.
- Per-component R/W access badges from the entity inspector.
- Stale diagnostic descriptors: TRECS006, 010, 011, 014, 017, 018, 019, 021, plus reserved-but-never-shipped gaps.
- `AccessorRole.Bypass` / `AccessorRole.Input` (use `Unrestricted`; input permissions auto-derived from `[ExecuteIn(Input)]`).
- `World.CreateAccessor(string)` overload.
- `RemoveAllEntitiesOnDispose` setting.

## [0.1.0] - 2026-04-18

### Added

- Initial release
