
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-05-07

This release covers a large redesign pass on accessor permissions, source-gen attribute ergonomics, native collections, and the editor tooling. All breaking changes are mechanical text-level migrations unless otherwise noted.

### Added

- **Accessor roles.** New `AccessorRole` enum (`Fixed`, `Variable`, `None`) replaces the previous `SystemPhase` / `IsEditor` parameters on accessor APIs. The role drives component read/write rules, structural-change rules, and heap-allocation rules in one place. See the new [Accessor Roles](core/accessor-roles.md) doc for the full matrix.
- **Trecs Player** editor window. Replaces the older time-travel shell with a unified record / playback / snapshot / scrub / fork / loop UI, plus a Saves library window for managing recordings and snapshots side by side. Settings (e.g. `PlaybackEndAction`) persist via `EditorPrefs` and are reachable outside play mode.
- **Hierarchy window.** First-class hierarchy editor window with persisted expand/collapse state, search-scope predicates, identity-based selection that survives world transitions and domain reloads, and a per-entity component inspector with JSON edit. See [Hierarchy](editor-windows/hierarchy.md).
- **`WorldRegistry`** — static registry of active `World` instances for editor tooling. Worlds register on `Initialize` and unregister on `Dispose`. `World.DebugName` / `WorldBuilder.SetDebugName(string)` let you give worlds human-readable names that surface in editor dropdowns.
- **System-control APIs.** `World.SetSystemEnabled(int systemIndex, EnableChannel, bool)` toggles a non-deterministic disable channel (multi-channel, AND semantics — a system runs only when no channel disables it). For deterministic, replay-safe pauses, use the new `WorldAccessor.SetSystemPaused`. `World.IsSystemEffectivelyEnabled(int)` is a single "would this system run on the next tick" query for debug UIs and tests.
- **`WorldAccessor.StepFixedFrame`** exposes single-frame stepping outside the system runner.
- **`EntityHandle.TryToEntity(WorldAccessor)`** overload alongside the existing `EntityQuerier`-based form.
- **Positional-tag attribute form.** `[ForEachAspect]`, `[ForEachEntity]`, `[SingleEntity]`, and `[FromWorld]` now accept tags as positional generic arguments — e.g. `[ForEachEntity<EcsTags.Enemy>]` — in addition to the existing `typeof(...)` form. Samples and docs are migrated; both forms remain supported.
- **`[SingleEntity]` is now per-parameter / per-field** and works in four contexts: plain `Execute`, mixed with `[ForEachEntity]`, `[WrapAsJob]` auto-generated jobs, and hand-written job-struct fields. See [Systems — SingleEntity](core/systems.md#singleentity).
- **`UnsafeRingDeque<T>`** — Burst-compatible growable double-ended ring buffer.
- **`19_MultipleWorlds` sample** demonstrating multiple `World` instances in a single Unity scene.
- **Source-gen diagnostics.** ~100 structured diagnostics across the source generators (TRECS001–TRECS115), each with a dedicated test, grouped by area: ForEach, Aspect, Component, Template, AutoSystem, Iteration, Hook migration, Job scheduling, FromWorld, WrapAsJob / AutoJob, SingleEntity. A compile-cleanliness harness now keeps all generators warning-free under `treat-warnings-as-errors`.
- **`[Serializable]` is auto-emitted on `IEntityComponent` partials**, so components round-trip through any reflection-based serializer without manual annotation.

### Changed (breaking)

- **`[ExecutesAfter]` / `[ExecutesBefore]` renamed to `[ExecuteAfter]` / `[ExecuteBefore]`.** Properties `ExecutesAfterSystems` / `ExecutesBeforeSystems` likewise become `ExecuteAfterSystems` / `ExecuteBeforeSystems`. Migration: project-wide text rename.
- **`[Phase(...)]` renamed to `[ExecuteIn(...)]`.** Matches the imperative-form convention of `[ExecuteAfter]` / `[ExecuteBefore]` / `[ExecutePriority]`. The underlying `SystemPhase` enum and `Phase` property name on the attribute are unchanged — only the attribute class is renamed. Migration: project-wide text rename `[Phase(` → `[ExecuteIn(` and `PhaseAttribute` → `ExecuteInAttribute`.
- **`SetDef` renamed to `EntitySet`** (and the internal storage struct to `EntitySetStorage`). Set declarations on templates now use the new name. Migration: project-wide text rename.
- **Accessor creation API.** `World.CreateAccessor(string)` is deprecated and `World.CreateAccessor<T>()` / `World.CreateAccessor(Type)` (the system-type-derived overloads) are now framework-internal — end-user code should always use `World.CreateAccessor(AccessorRole.Unrestricted, debugName)` for non-system code (lifecycle hooks, debug tooling, event callbacks, networking, scripting bridges). Systems get their accessor automatically from `Initialize`. Manually-created accessors never gain input-system permissions; only `[ExecuteIn(Input)]` system-owned accessors can call `AddInput<T>` and allocate from the frame-scoped heap.
- **`[SingleEntity]` is per-parameter / per-field.** The previous method-level form (`[SingleEntity(typeof(Tag))] void Execute(...)` decorating an entire method) no longer compiles. Migration: move the attribute from the method onto the specific parameter (or field) that should be resolved as the singleton.
- **`[VariableUpdateOnly]` is enforced symmetrically.** Writes are now blocked from non-`Fixed` phases just as reads were already restricted to certain phases, and `[Constant]` writes are blocked outside the fixed-update phase. Variable-phase systems can no longer perform structural mutations on entity Sets. `[VariableUpdateOnly]` is also recognized at the template scope; **support on entity sets has been removed** (templates are the right granularity). Fixed-role observer registration on `[VariableUpdateOnly]` template groups is rejected at registration time.
- **`[FixedUpdateOnly]` attribute removed.** Fixed-update systems already have full write permission, so the explicit marker was redundant. Migration: drop the attribute; rules are unchanged.
- **Phase-less accessor escape removed.** Accessors no longer have a "no role" mode that bypasses VUO/Constant/structural rules — every accessor has a role. Migration: pick `AccessorRole.Unrestricted` for accessors that genuinely need to operate outside system execution; otherwise pick `Fixed` or `Variable` and let rule violations surface.
- **Single-accessor rule enforced during fixed-phase execute.** A fixed-phase system can hold only one live accessor at execute time. Tooling and lifecycle code that previously created throwaway accessors mid-tick should be reworked to use the system's own accessor.
- **Aspect interfaces are detected via the `IAspect` marker** instead of an `[AspectInterface]` attribute. The attribute and the `AspectValidator.ValidateUsagePatterns` validator are deleted. Migration: change `[AspectInterface] interface IFooAspect { ... }` to `interface IFooAspect : IAspect { ... }`.
- **`ITemplate` field declarations must omit an access modifier** (TRECS034 reworked). The generator now suppresses CS0169 / CS0414 / CS0649 / IDE0051 / IDE0052 on these fields, so the implicit-access form is the only correct way to declare template fields.
- **Native collection cleanup.** `NativeRingBuffer<T>` (managed class) becomes `NativeRingDeque<T>` (struct, Burst-compatible). The managed `RingBuffer<T>` is renamed to `RingDeque<T>` and aligned with the native sibling. Sequence-shape collections (rings, deques, dense buffers) now standardize on a signed-`int` `Length` property — `Capacity` / `Count` / `uint Count` are unified. `IsEmpty()` is now a property across all Trecs collections. `NativeBuffer` collapses its read/write pointer accessors into a single typed `GetRawPointer`. `NativeDenseDictionary` gains modern native-container affordances. `SimpleResizableBuffer<T>` is inlined into `DenseDictionary` as plain `T[]` arrays. Migration: rename per the table above; replace any `IsEmpty()` calls with the property.
- **"Bookmark" terminology replaced with "snapshot"** across runtime and editor APIs (e.g. `TrecsBookmarksWindow` → `TrecsSavesWindow`). Migration: text rename `Bookmark` → `Snapshot` / `Saves`.
- **Fast-forward API targets a frame, not a time.** `SystemRunner` fast-forward now takes a target frame number and runs catch-up frames until the simulation reaches it; the per-tick fixed-update cap is lifted during catch-up so the operation actually finishes promptly.
- **`SystemRunner.StepFrame` renamed `StepFixedFrame`** and re-exposed on `WorldAccessor` for direct use from systems.
- **`ISystem.OnReady` runs in execute order**, not registration order: Input → Fixed → EarlyPresentation → Presentation → LatePresentation, with `[ExecuteAfter]` / `[ExecuteBefore]` / `[ExecutePriority]` applied within each phase. Code that depended on registration-order timing for `OnReady` will need explicit `[ExecuteAfter]` constraints.
- **`SvProfiling` renamed to `ProfileBlocks`.** Migration: text rename.
- **Binary serialization API.** `WriteBinary` / `SerializableByteArray` are replaced by a simpler `WriteBytes(byte[])` / `ReadBytes` pair. Migration: rewrite call sites to the new method names; the wire format is unchanged for byte arrays.
- **Aspect-interface generator** no longer emits the legacy `[AspectInterface]`-driven validator code path; aspects discovered via `IAspect` go through a single registration code path.
- **Public serializer types are now `sealed`.** Every public `ISerializer<T>` implementation across `com.trecs.serialization` is sealed; same for `com.trecs.svkj`'s `DictionarySerializer` / `HashSetSerializer` / `AssetReferenceSerializer`. Inheriting from these types was never a supported extension point.
- **`DictionarySerializer<TKey, TValue>` and `HashSetSerializer<T>` moved to `com.trecs.svkj`.** They wrap managed `Dictionary` / `HashSet` whose iteration order isn't stable across runs — silent desync risk if used on simulation state. Tests and internal callers continue to work since their asmdefs already reference `Trecs.Svkj`. Public users wanting deterministic equivalents should use `DenseDictionarySerializer` / `DenseHashSetSerializer`.
- **`BitWriter` moved from internal `Trecs.Serialization` to `Trecs.Internal` (public sealed, `[EditorBrowsable(Never)]`)** to mirror `BitReader`. Same hidden-public visibility model.
- **Several serializers promoted from internal to public sealed** for uniformity with the rest of the family: `StringSerializer`, `TypeSerializer`, `RngSerializer`, `BlobMetadataSerializer`, `BlobManifestSerializer`.
- **`SerializationBuffer` inner accessors made internal.** `.Reader` / `.Writer` / `.BinaryReader` / `.BinaryWriter` are no longer public — they all carried "you shouldn't need to use this directly" comments and had no external callers, so they're framework-internal.
- **`RecordingMetadata`** now uses init-setter syntax instead of a 6-parameter positional constructor, matching `SnapshotMetadata`.
- **`NativeArraySerializer<T>` / `NativeListSerializer<T>`** accept an `Allocator` constructor parameter (defaulting to `Persistent`). Existing `RegisterSerializer<NativeArraySerializer<X>>()` registrations keep working; callers wanting a custom allocator can do `RegisterSerializer(new NativeArraySerializer<X>(Allocator.Temp))`.

### Deprecated

- `World.CreateAccessor(string)` — use `World.CreateAccessor(AccessorRole, string)`. Slated for removal in a future release.
- `SerializationBuffer.GetMemoryStreamHash()` — use `ComputeChecksum()` (same hash, returned as `uint` instead of sign-cast `int`).

### Changed

- **Package layout.** `com.trecs.tools` is removed; its contents moved into `com.trecs.core/Editor` (hierarchy window, shared editor helpers) and `com.trecs.serialization` (record / playback editor windows, runtime recorders). Migration: drop the `com.trecs.tools` entry from your `Packages/manifest.json` — the same types are available through `com.trecs.core` and `com.trecs.serialization`.
- **`com.trecs.serialization`** now owns runtime recorders, the snapshot serializer, and the player / saves editor windows alongside the existing serialization core.
- **`TrecsAutoRecorder`** honors `LoopPlayback` and pauses at the tail in scrubbed-back-from-live mode, matching live-mode behaviour.
- **Source-gen polish.** `ForEach` / `ForEachAspect` / `RunOnce` generators correctly handle nested-class scope; `FromWorld` tag parsing is consolidated and dead code paths removed; the generator reads generic-attribute `TypeArguments` so the new positional-tag form works everywhere; the `[SingleEntity]` emit path for plain `Execute` and `[ForEachEntity]` is unified; aspect parallel-for restrictions warn rather than silently mis-emit.
- **`TrecsSerialization{Reader,Writer}Adapter`** are now public, so external tooling can drive the queue serializer directly.
- **Schema cache** (used by the hierarchy window) persists per-accessor access data, accessor execution order, and structural ops across play sessions; output is deterministic JSON and merges cleanly across runs.
- **System-effectively-enabled** display: hierarchy rows are grayed when a system is effectively disabled by any channel or pause.
- **Submission pipeline** flushes Set job writes via a new `EntitySubmitter.FlushAllSetJobWrites` passthrough; structural-change observers in cascades have pinned ordering invariants.

### Removed

- `com.trecs.tools` package (see Changed for migration).
- `[FixedUpdateOnly]` attribute.
- `[AspectInterface]` attribute and `AspectValidator.ValidateUsagePatterns`.
- `AccessorRole.Bypass` (renamed to `AccessorRole.Unrestricted` — was briefly named `None` mid-cycle) and `AccessorRole.Input` (input permissions are auto-derived from `[ExecuteIn(Input)]` and not separately selectable).
- Legacy `Group.cs` runtime type — `GroupIndex` is the canonical handle.
- `FixedTypeCommon.cs` (consolidated into the existing fixed-type helpers).
- Per-component R/W access badges from the entity inspector (the data is still available in the access tracker; the badge was visually noisy).
- Source-gen dead diagnostic descriptors `TRECS006` / `010` / `011` / `014`.
- `SerializableByteArraySerializer` (replaced by direct `WriteBytes` / `ReadBytes`).
- Stale "JSON serialization" references throughout `ISerializationReader` / `ISerializationWriter` / `DenseDictionarySerializer` doc-comments. Only the binary path is supported.

### Fixed

- `[SingleEntity]` correctness gaps surfaced in review (parameter-resolution ordering, `[WrapAsJob]` static-method param wiring, and hand-written job-struct field handling).
- Hierarchy tree: stop applying Unity's name-nicification to template display names; route generic-struct component fields through the read-only fallback so the inspector renders them.
- Trecs Player: refuse `Step` past the recorded tail with status feedback; clear pending step on `FixedIsPaused` unpause; fix Loop toggle bug; demoted past-target fast-forward log to Trace.
- Serialization: auto-divert abstract `T` in `Read<T>` / `Write<T>` / `WriteDelta<T>` to the `WriteObject` / `ReadObject` path so writer and reader formats stay paired; tighten diagnostics around the abstract-T divert; drop the speculative `IEquatable<T>` assert in `WriteDelta<T>`.
- Hierarchy window: persist tree-row selection across world transitions, persist expand/collapse state across play-mode entry and domain reloads, sort accessor rows by the runner's actual execution order, disambiguate duplicate-name template rows, clear `TreeView` selection on id-space resets, validate selection-proxy payload before short-circuiting Unity's stop-play restore.
- `Trecs.Serialization.SourceGen`: ship `SRZ008` / `SRZ009` / `SRZ010` rules in `AnalyzerReleases.Shipped.md` (the rules existed in code but the release-tracker was stale, tripping `RS2008`). Drop trailing periods on three single-sentence diagnostic messages so the build is `RS1032`-warning-free.
- `BinarySerializationReader` / `BinarySerializationWriter`: `ResetForErrorRecovery` now clears `_version` / `_includesTypeChecks` (reader) and `_version` / `_includeTypeChecks` (writer) — symmetric with `Start`.
- `BinarySerializationWriter.NumBytesWritten` ceiling-divides the bit count (was integer-dividing, under-reporting by up to 7 bits).
- `EnumSerializer<T>`: replace `Convert.ToXxx`-based dispatch with a cached `UnderlyingKind` switch + `Unsafe.As<T, primitive>` — no boxing on the hot path. Assert at static init that the enum has no aliased values (delta encoding silently mis-mapped them otherwise) and surface the 256-value delta-encoding cap up front via a `values.Length` check.
- `MemoryBlitter`: assert little-endian platform at static init. Saved files are byte-identical via `Buffer.MemoryCopy`, so a BE platform would silently produce incompatible payloads.
- `TrecsAutoRecorderSettings.OverflowAction` xml-doc said the default was `Pause` but the field initializer was `DropOldest`. Doc and reality now agree (DropOldest is the default; the safety narrative reflects eviction).
- Recording sentinel name: `RecordingHandler` writes `"recordingSentinel"`, `PlaybackHandler` reads `"recordingSentinel"` — was inconsistent (`"sentinel"` on the read side). Wire format unaffected.
- `RingDeque` / `NativeRingDeque` serializers: the field-name string is `"count"` everywhere (was `"numItems"`), matching the rest of the collection serializers.

## [0.1.0] - 2026-04-18

### Added

- Initial release
