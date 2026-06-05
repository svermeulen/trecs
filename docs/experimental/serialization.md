# Serialization

!!! warning "Experimental"
    The custom-serializer surface on this page (`ISerializer<T>`, `IComponentArraySerializer<T>`, `SerializerRegistry`, `ComponentArraySerializerRegistry`, `ICustomWorldStateSection`) is experimental and may change in future 0.x releases.

Components are unmanaged structs, so they round-trip to bytes automatically — the framework treats them as plain memory. The two cases where you write serialization code yourself are:

1. A component points at a **managed** type (a `class`, or a struct with managed fields) via the [heap](pointers.md) — e.g. `SharedPtr<MyClass>`, `UniquePtr<MyClass>`, anything whose payload contains a `string`, `List<T>`, or another reference type. Register an `ISerializer<T>` against [`world.SerializerRegistry`](#registering-the-registry).

2. A component holds an **unmanaged native container** like `UnsafeHashMap<TKey,TValue>` or `UnsafeList<T>`, or you want to **skip** serializing the component entirely (transient single-frame state, runtime handles to reset on load). Register an `IComponentArraySerializer<T>` against [`world.ComponentArraySerializerRegistry`](#custom-component-array-serialization).

For everything else there is nothing to do — the [Trecs Player window](../editor-windows/player.md) records, scrubs, and replays world state without any setup beyond the world itself.

## Registering the registry

Every `World` owns a `SerializerRegistry`, pre-populated with all built-in primitive, math, ECS, and recording-metadata serializers. Add your own via `world.SerializerRegistry` after `WorldBuilder.Build()` and before `world.Initialize()`:

```csharp
var world = new WorldBuilder()
    .AddTemplate(myTemplate)
    .Build();

// Register one ISerializer<T> per managed type you store on the heap:
world.SerializerRegistry.RegisterSerializer(new PatrolRouteSerializer());
world.SerializerRegistry.RegisterSerializer(new QueueSerializerUnmanaged<Vector3>());

world.Initialize();
```

You can also register them up front on the builder:

```csharp
var world = new WorldBuilder()
    .AddTemplate(myTemplate)
    .RegisterSerializer(new PatrolRouteSerializer())
    .RegisterSerializer(new QueueSerializerUnmanaged<Vector3>())
    .BuildAndInitialize();
```

If the type happens to be blittable (no managed fields), you can skip the custom serializer entirely — register the built-in `BlitSerializer<T>` against the registry directly (`world.SerializerRegistry.RegisterSerializer(new BlitSerializer<T>())`). For enums, register `EnumSerializer<T>` the same way. The common primitives (`int`, `float`, `string`, `Vector3`, `quaternion`, …) are pre-registered.

For common collections, Trecs ships generic closed-type serializers in `Trecs.Serialization`. Each comes in two variants: a `*Unmanaged` form for unmanaged element types (serialized as a single blit — always prefer this when it applies) and a `*Managed` form for class element types (serialized per-element via the registered serializer) — `ArraySerializerManaged<T>` / `ArraySerializerUnmanaged<T>`, `ListSerializerManaged<T>` / `ListSerializerUnmanaged<T>`, `QueueSerializerManaged<T>` / `QueueSerializerUnmanaged<T>`, `IterableDictionarySerializerManaged<TKey, TValue>` / `IterableDictionarySerializerUnmanaged<TKey, TValue>`. `IterableHashSetSerializer<T>` (along with `NativeArraySerializer<T>`, `NativeListSerializer<T>`, and `UnsafeListSerializer<T>`) has no suffix because its elements are always unmanaged. Register the closed type once and the registry handles it (e.g. `new ListSerializerUnmanaged<int>()`, `new QueueSerializerUnmanaged<Vector3>()`). Only author your own `ISerializer<T>` when the payload isn't covered by one of these.

## Authoring a custom serializer

Implement `ISerializer<T>` with paired `Serialize` and `Deserialize` methods. The binary format is purely positional — `Deserialize` must call the same readers in the same order as `Serialize` calls writers.

As a concrete example, take a managed class holding a list of waypoints, allocated via `SharedPtr.Register(world, blobId, value)` + `SharedPtr.Acquire<PatrolRoute>(world, blobId)` and stored on a component as `SharedPtr<PatrolRoute>`:

```csharp
public class PatrolRoute
{
    public List<Vector3> Waypoints;
    public float Speed;
}

public sealed class PatrolRouteSerializer : ISerializer<PatrolRoute>
{
    public void Serialize(in PatrolRoute value, ISerializationWriter writer)
    {
        writer.Write("count", value.Waypoints.Count);
        foreach (var waypoint in value.Waypoints)
        {
            writer.Write("waypoint", waypoint);
        }
        writer.Write("speed", value.Speed);
    }

    public void Deserialize(ref PatrolRoute value, ISerializationReader reader)
    {
        value ??= new PatrolRoute { Waypoints = new() };
        var count = reader.Read<int>("count");
        value.Waypoints.Clear();
        for (int i = 0; i < count; i++)
        {
            value.Waypoints.Add(reader.Read<Vector3>("waypoint"));
        }
        value.Speed = reader.Read<float>("speed");
    }
}
```

!!! note "Field names are debug-only"
    The `name` arguments to `writer.Write` / `reader.Read` are **not** persisted — they only surface in debug memory tracking and desync diagnostics. Reads must match writes by position, not by name.

If `value` is a struct with managed fields rather than a class, the `??=` line is unnecessary — `Deserialize` just populates the fields directly.

## Versioning

If the type's shape changes between releases, bump a version on the write side and branch on `reader.Version` to keep loading older saves:

```csharp
public void Deserialize(ref PatrolRoute value, ISerializationReader reader)
{
    value ??= new PatrolRoute { Waypoints = new() };
    var count = reader.Read<int>("count");
    value.Waypoints.Clear();
    for (int i = 0; i < count; i++)
    {
        value.Waypoints.Add(reader.Read<Vector3>("waypoint"));
    }
    value.Speed = reader.Read<float>("speed");

    // v2 added the Reverse flag; older saves default it to false.
    value.Reverse = reader.Version >= 2 && reader.Read<bool>("reverse");
}
```

`ISerializationWriter.Version` is available on the write side for the symmetric branch.

## Custom component-array serialization

`ISerializer<T>` covers managed types on the heap. `IComponentArraySerializer<T>` covers the other side: overriding how the framework serializes the **array of values** for one specific component type during snapshots, recordings, and checksums.

Three common reasons to register one:

- **Skip a component entirely.** Transient single-frame state — collision scratchpads, broadphase buffers, debug overlays — that you don't want to persist and don't want contributing to determinism checksums.
- **Reset on load instead of restoring.** Runtime handles (physics simulations, audio voices, third-party engine objects) where serializing the internal state isn't possible or meaningful; on deserialize you reinitialize a fresh handle.
- **Walk a native container.** A component holding an `UnsafeHashMap<K,V>`, `UnsafeList<T>`, or similar — these can't be byte-blitted because their backing memory lives outside the component struct.

Every `World` exposes a `ComponentArraySerializerRegistry` for this:

```csharp
world.ComponentArraySerializerRegistry.Register(new MyPhysicsWorldSerializer());
```

!!! note "Register before `world.Initialize()`"
    These registrations change the snapshot wire format, so they are part of the [world schema](#schema-compatibility-for-snapshots-and-recordings) and the registry **seals at `world.Initialize()`** — registering or unregistering after that throws. Register between `Build()` and `Initialize()` as above, or up front via `WorldBuilder.RegisterComponentArraySerializer(...)`. (Allowing mid-session changes would change the wire format mid-session, stranding snapshots taken earlier in the same run — e.g. the editor's rewind keyframes.)

The interface hands you a `NativeList<T>` view of the component values for the partition being serialized — a familiar, Burst-compatible Unity Collections type. The framework owns the entry count (every component array in a partition has the same length, one entry per entity) and writes it for you, so you only deal with per-element data:

```csharp
public interface IComponentArraySerializer<T> where T : unmanaged, IEntityComponent
{
    void Serialize(NativeList<T> values, ISerializationWriter writer);
    void Deserialize(NativeList<T> values, int requiredCount, ISerializationReader reader);
}
```

On `Serialize`, `values.Length` equals the current entity count. On `Deserialize`, `values.Length` is the **live world's** current count and `requiredCount` is the count the array must have on return — you decide how to reconcile (preserve in place when counts match, resize-and-default-init, dispose-and-rebuild, etc.). The dispatcher asserts `values.Length == requiredCount` after you return.

### Skipping a component entirely

The skip case is common enough to ship as a one-liner. `SkipComponentSerializer<T>` skips writing/reading the component's values and leaves the live array's contents untouched on load:

```csharp
using Trecs.Serialization;

world.ComponentArraySerializerRegistry.Register(new SkipComponentSerializer<CBroadphaseScratch>());
world.ComponentArraySerializerRegistry.Register(new SkipComponentSerializer<CDebugOverlay>());
```

Because no value bytes are written, the component contributes nothing of substance to the stream — and therefore nothing to the checksum hash — so determinism checks won't desync on values that vary between runs. This is the simplest way to keep transient per-frame state out of recordings.

The framework still writes the entry **count** (a single `int`) for every component array, and `SkipComponentSerializer<T>` asserts on load that `requiredCount` matches the live array's length. A snapshot taken when a partition had _N_ entities must be restored into a live world where that partition also has _N_ entities. If the counts diverge — e.g. the user added more entities of that template between save and load — the deserialize throws. Without this check the skipped component array would silently desync from the rest of the partition, corrupting entity-component lookups downstream.

`SkipComponentSerializer<T>` is right for the in-session save/restore case, where the same entities exist on both sides and you want to preserve their runtime state. For fresh-load-from-disk scenarios — where the live world starts empty and the snapshot brings the entities into existence — use `DefaultValueComponentSerializer<T>` instead:

```csharp
world.ComponentArraySerializerRegistry.Register(new DefaultValueComponentSerializer<CMyTransient>());
```

It also writes nothing per element, but on load **resizes** the array to `requiredCount` and zero-inits every entry. The values still contribute nothing to the stream's bytes (the checksum hash skips them too), but they're regenerated to `default(T)` on load rather than asserting on a count mismatch. Use it for components whose entries the runtime re-initializes from elsewhere (system init, OnAdded handlers, the next tick's update) — _not_ for components whose live runtime state is the source of truth.

### Reusing an `ISerializer<T>` for per-element dispatch

When you already have an `ISerializer<T>` for a component type — for example a versioned per-element format used elsewhere — wrap it in `PerEntityComponentArraySerializer<T>` rather than copying the logic into a new `IComponentArraySerializer<T>`:

```csharp
world.ComponentArraySerializerRegistry.Register(
    new PerEntityComponentArraySerializer<CMyComponent>(new MyComponentSerializer()));
```

The framework still owns the count; the inner `ISerializer<T>` is called once per entity. This costs one virtual call per entity vs. one per array — fine for snapshots and recordings, but prefer a dedicated `IComponentArraySerializer<T>` (one loop, no inner virtual dispatch) on hot rollback paths over large groups.

### Conditional skipping (flag-based)

To exclude a component's data from specific serialization contexts — while still serializing them normally in others — define a user flag at `SerializationFlags.FirstUserBitIndex` or higher and branch on `writer.HasFlag(...)`:

```csharp
// Define a user flag
public static class MyAppFlags
{
    public const long IsForChecksum = 1L << (SerializationFlags.FirstUserBitIndex + 0);
}

public void Serialize(NativeList<CMyComponent> values, ISerializationWriter writer)
{
    if (writer.HasFlag(MyAppFlags.IsForChecksum))
    {
        return; // skip per-element data; the framework still writes the count
    }
    for (int i = 0; i < values.Length; i++) { /* normal writes */ }
}
```

Pass the flag when creating the writer for that context. The read side only needs a symmetric branch if the same flag combination is ever deserialized.

### Reset on load

When a component wraps a runtime handle that should be reinitialized rather than restored, write nothing on `Serialize` and reset the existing instance in place on `Deserialize` — keeping the handle's native allocations intact:

```csharp
public sealed class PhysicsWorldSerializer : IComponentArraySerializer<CPhysicsWorld>
{
    public void Serialize(NativeList<CPhysicsWorld> values, ISerializationWriter writer) { }

    public void Deserialize(
        NativeList<CPhysicsWorld> values,
        int requiredCount,
        ISerializationReader reader)
    {
        // Component lives on a single global-template entity, so both
        // values.Length and requiredCount are always 1. Reset the
        // PhysicsWorld in place rather than discarding it and creating a
        // new one — this preserves the handle's existing native allocations.
        values.ElementAt(0).PhysicsWorld.Reset();
    }
}
```

### Walking a native container

For a component with an `UnsafeHashMap<K,V>` (or any other native container the framework can't blit), iterate it explicitly. Use the same write-then-read pattern you'd use for `ISerializer<T>`, but operating over each element of the `NativeList<T>`:

```csharp
public partial struct CCollisionGroup : IEntityComponent
{
    public CollisionTagSet TagSet;
    public UnsafeHashMap<CollisionPair, CollisionInfo> Existing;
}

public sealed class CollisionGroupSerializer : IComponentArraySerializer<CCollisionGroup>
{
    public void Serialize(NativeList<CCollisionGroup> values, ISerializationWriter writer)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ref readonly var group = ref values.ElementAt(i);
            writer.Write("tagSet", group.TagSet);
            writer.Write("pairCount", group.Existing.Count);
            foreach (var kv in group.Existing)
            {
                writer.Write("pair", kv.Key);
                writer.Write("info", kv.Value);
            }
        }
    }

    public void Deserialize(
        NativeList<CCollisionGroup> values,
        int requiredCount,
        ISerializationReader reader)
    {
        // Dispose any native containers the existing elements own before we
        // overwrite the slots, then rebuild from the stream.
        for (int i = 0; i < values.Length; i++)
        {
            values[i].Existing.Dispose();
        }
        values.Resize(requiredCount, NativeArrayOptions.ClearMemory);
        for (int i = 0; i < requiredCount; i++)
        {
            ref var group = ref values.ElementAt(i);
            group.TagSet = reader.Read<CollisionTagSet>("tagSet");
            var pairCount = reader.Read<int>("pairCount");
            group.Existing = new UnsafeHashMap<CollisionPair, CollisionInfo>(pairCount, Allocator.Persistent);
            for (int p = 0; p < pairCount; p++)
            {
                var key = reader.Read<CollisionPair>("pair");
                var info = reader.Read<CollisionInfo>("info");
                group.Existing.Add(key, info);
            }
        }
    }
}
```

The same versioning rules apply as for `ISerializer<T>`: read what you wrote in the same order, branch on `reader.Version` if the layout has changed.

## Custom world-state sections

Everything above customizes how *ECS-owned* data serializes. Some games also have deterministic state that lives entirely outside the ECS — most commonly a scripting VM (Lua, a behavior-tree interpreter, …) whose internal state co-evolves with the simulation. If that state isn't captured alongside the world, every world-state restore silently desyncs it: a rewind scrub restores the entities but not the script state that drives them.

For this, register an `ICustomWorldStateSection`:

```csharp
public interface ICustomWorldStateSection
{
    void Serialize(ISerializationWriter writer);
    void Deserialize(ISerializationReader reader);
}
```

```csharp
builder.RegisterCustomWorldStateSection("MyVmState", new MyVmStateSection(vm));
// or between Build() and Initialize():
world.CustomWorldStateSections.Register("MyVmState", new MyVmStateSection(vm));
```

Registered sections are appended to the world-state stream after the built-in sections, in registration order. Because the registration lives on the `World` itself, **every** capture path includes the section automatically — snapshots, rewind keyframes, recordings, desync checksums, and the editor's Player-window rewind tooling. You never wrap or replace the framework's serializer.

The framework owns the framing: each section is written inside its own scope, prefixed with a hash of its registration name (so a mismatched section set is reported by name) and followed by a guard byte (so wire drift inside one section is pinned to that section instead of cascading into the next as garbage).

!!! note "Register before `world.Initialize()`"
    Like component-array serializers, custom sections change the snapshot wire format, so they are part of the [world schema](#schema-compatibility-for-snapshots-and-recordings): the registry seals at `world.Initialize()`, the section names (and their order) are folded into the `WorldSchemaFingerprint`, and a snapshot saved with a different section set fails loudly at load with the diverging aspect named. The registration *name* is the section's stable wire identity — treat a rename like any other wire format change.

`Serialize` and `Deserialize` must consume exactly mirrored data, and the data must be a deterministic function of game state — it participates in desync checksums. Sections are deserialized after all built-in sections, so the ECS world is fully restored by the time your `Deserialize` runs; the world's `DeserializeCompletedEvent` fires after custom sections, so listeners observe a fully consistent world + section state.

## Explicit type IDs

By default, Trecs derives type identifiers from `BurstRuntime.GetHashCode32(type)` — a hash of the fully qualified type name. Renaming or moving a type to a different namespace changes the hash and silently corrupts saved files that reference the old ID.

For projects that need guaranteed save-file compatibility across refactors, define `TRECS_REQUIRE_EXPLICIT_TYPE_IDS` in your project's scripting define symbols. With this define:

- Every serialized type must carry a `[TypeId(int)]` attribute with a stable integer ID.
- Common primitives, Unity math types, and Trecs internals are pre-registered.
- Generic types compose their ID from the definition's ID and each type argument's ID.
- Types without a `[TypeId]` throw at runtime instead of silently using a name-derived hash.

```csharp
[TypeId(123456)]
public class PatrolRoute { /* ... */ }
```

This makes type identity independent of namespaces and class names — refactors never corrupt save files, and any data-shape changes are handled by versioned custom serializers.

## Schema compatibility for snapshots and recordings

A snapshot or recording is a raw binary image of the world, and its wire format depends on the world **schema** matching exactly between save and load:

- the set of templates, their tags, and their components (names, declaration order, and struct sizes),
- each component's `[VariableUpdateOnly]` status,
- the registered entity sets,
- which component types have a custom `IComponentArraySerializer<T>` registered,
- the registered [custom world-state sections](#custom-world-state-sections) (names, in registration order).

The loaded *system list* is deliberately **not** part of the schema: per-system paused state is saved sparse and by system identity, so adding, removing, or reordering systems keeps old snapshots loadable (a pause for a system that no longer exists is dropped with a warning). Note that changing what your systems *do* still changes replay behavior — recordings surface that at runtime as a desync checksum mismatch, which is the right layer for behavioral drift.

Every snapshot and recording stamps a `WorldSchemaFingerprint` — a compact hash of all of the above — into its metadata at save time. On load, Trecs validates it against the live world **before reading any world state**. A mismatch throws a `SerializationException` that names which aspect of the schema diverged (groups/components, sets, custom serializers, or custom sections) and shows both fingerprints:

```text
This snapshot was saved with a different world schema and cannot be loaded — ...
 - Groups/components: a component or tag type was added, removed, or renamed
   on a template; a component struct's size changed; ...
Saved:   WorldSchemaFingerprint(Groups:A716E664064B9EE2 ...)
Current: WorldSchemaFingerprint(Groups:3E27FE46A2A729B6 ...)
```

There is no in-place migration for schema-stale files — re-save the snapshot (or re-record) from a world built with the current schema. To check compatibility up front without attempting a load, compare `world.SchemaFingerprint` against `SnapshotSerializer.PeekMetadata(...).SchemaFingerprint` or `RecordingBundleSerializer.PeekHeader(...).SchemaFingerprint`.

The fingerprint guards the **world layout**; the *contents* of heap-stored managed types are still your custom serializers' concern — evolve those with [`reader.Version`](#versioning) branching, which the fingerprint deliberately does not cover.

!!! note "Field reorders and renames"
    The fingerprint hashes each component's type identity, byte size, **and** a compile-time field-layout hash (the source generator emits one on every component). So reordering two `float` fields inside a component — which keeps the size identical but changes where the bytes land — *does* invalidate old snapshots, with a clear "Groups/components" mismatch rather than silent corruption. A pure field **rename** is deliberately allowed (only field types and order are hashed, not names), since it doesn't change the blit layout. Components you hand-write outside the source generator fall back to size-only detection, so treat a same-size edit to those as a schema change and regenerate your snapshots.

## Save games and game patches

World-state snapshots are **same-build artifacts**. Within one build of your game they make excellent saves — quicksaves, autosaves, crash recovery — because a memory image is fast to write, fast to restore, and perfectly faithful. But they are *not* a patch-durable save-game format: the wire is a raw image of your schema, so the first patch that adds a field to a component, adds a component to a template, or renames a tag invalidates every save your players have made. There is no in-place migration, by design — see the [migration trade-off](#schema-compatibility-for-snapshots-and-recordings) above.

If your game needs saves that survive patches, **author a domain-level save format** on top of Trecs rather than persisting world state directly:

- **Save the facts, not the memory.** Serialize the logical state your game needs to reconstruct a session — player position, inventory ids, quest flags, world seed — using your own format, or Trecs' `ISerializer<T>` machinery with [`reader.Version`](#versioning) branching for evolution.
- **Rebuild, don't restore.** On load, create a fresh world and repopulate it through normal gameplay paths (`AddEntity` + templates + component sets). Schema changes between patches are then absorbed by ordinary game code: new components get their template defaults, removed components simply aren't written, renames don't matter because your save format never stored Trecs type identities.
- **Version explicitly.** Bump your own save-format version on breaking changes and branch on it at load — the same discipline as any other persistence format.

This is the standard architecture for patch-durable saves in shipped games generally — the engine-level state image is a runtime tool (rollback, replays, scrubbing), and the save system is a deliberately smaller, versioned projection of it that you control.

Trecs still helps at the boundary: stamp your save-format version into `SnapshotMetadata.Version` / `BundleHeader.Version` for any world-state payloads you *do* persist, and use `PeekMetadata` / `PeekHeader` plus `world.SchemaFingerprint` to detect stale files up front and show players a friendly "this save is from an older version" message instead of a load error.

## See also

- [Pointers](pointers.md) — pointer types and which kinds of data need to live on the heap.
- [Trecs Player Window](../editor-windows/player.md) — uses the registered serializers to record, scrub, and replay world state.
- [Sample 10 — Dynamic Collections](../samples/10-dynamic-collections.md) — `UniquePtr<Queue<Vector3>>` plus the built-in `QueueSerializerUnmanaged<Vector3>` registered against the world so the trail round-trips through snapshots / recording.
- [Sample 14 — Blob Seed Pattern](../samples/14-blob-seed-pattern.md) — `SharedPtr<ColorPalette>` with stable `BlobId`s (the sample itself doesn't take snapshots, but the same `ISerializer<T>` pattern from Sample 10 would apply).
