# Cascade Remove & Dispose-on-Remove

Trecs does **not** auto-free heap-backed component fields or auto-remove
entities referenced by a component — left to its own devices it leaks the
storage and orphans the references. Historically you wired this up by hand with
an [`OnRemoved` observer](entity-events.md) that disposed pointers and removed
referenced entities. The `[CascadeRemove]` and `[DisposeOnRemove]` field
attributes replace that boilerplate with two declarative annotations the source
generator turns into a removal handler for the component.

Both attributes target component fields and fire automatically during
[submission](structural-changes.md#when-submission-happens) when the entity
owning the component is removed — through *every* removal path: a single
`Remove`, a bulk `RemoveEntitiesWithTags` / `RemoveAllEntitiesInGroup`, the full
`RemoveAllEntities`, and the automatic pass during `World.Dispose()`.

```csharp
partial struct Owner : IEntityComponent
{
    [CascadeRemove, DisposeOnRemove]
    public TrecsList<EntityHandle> Children;
}

partial struct Child : IEntityComponent
{
    [CascadeRemove]
    public EntityHandle Parent;
}
```

## `[CascadeRemove]` — remove referenced entities

`[CascadeRemove]` marks a field that *references other entities* so those
entities are removed when the owner is removed — a turnkey cascade delete.

| Field type | Effect on owner removal |
|---|---|
| `EntityHandle` | The single referenced entity is removed. |
| `TrecsList<EntityHandle>` | Every referenced entity in the list is removed. |

```csharp
partial struct CascadeLink : IEntityComponent
{
    [CascadeRemove]
    public EntityHandle Target;     // removing the link removes its target
}
```

Key guarantees:

- **Stale handles are skipped.** A handle pointing at an already-removed entity
  is ignored (checked via `EntityHandle.Exists`), so a list mixing live and dead
  references removes only the live ones.
- **Nested chains tear down in one `Submit()`.** The cascade completes within the
  same `Submit()` as the owner's removal — queued child removals drain on
  subsequent submission iterations, so an `owner → child → grandchild` chain
  collapses atomically. This is bounded by `WorldSettings.MaxSubmissionIterations`
  (the same cap that governs [cascading structural changes from
  callbacks](entity-events.md#cascading-structural-changes-from-callbacks)).
- **Cycles are safe.** The underlying remove queue is idempotent, so a reference
  cycle (`a → b → a`) terminates rather than looping forever.

!!! note "Implies exclusive ownership"
    `[CascadeRemove]` unconditionally removes the referenced entities, so it
    models **exclusive ownership** — use it only when the owner is the sole owner
    of those entities. For a *referencing* relationship where the target should
    outlive the referrer (an AI targeting a unit, say), don't use
    `[CascadeRemove]`; guard the reference with `EntityHandle.Exists` at the read
    site instead.

`[CascadeRemove]` only *removes the referenced entities* — it never disposes the
field's own storage. A `TrecsList<EntityHandle>` still owns a heap allocation for
the list itself; pair it with `[DisposeOnRemove]` to free that too (see
[Composition](#composing-the-two-attributes)).

## `[DisposeOnRemove]` — free the field's storage

`[DisposeOnRemove]` marks a heap-backed field so its backing storage is disposed
when the owner is removed. The generated handler calls `field.Dispose(world)`
uniformly, and each type's own `Dispose` does the right thing:

| Field type | Effect on owner removal |
|---|---|
| `TrecsList<T>` | **Frees** the backing storage. |
| `UniquePtr<T>` | **Frees** the backing storage. |
| `NativeUniquePtr<T>` | **Frees** the backing storage. |
| `SharedPtr<T>` | **Decrements** the reference count, freeing only when it reaches zero. |
| `NativeSharedPtr<T>` | **Decrements** the reference count, freeing only when it reaches zero. |

```csharp
partial struct Holder : IEntityComponent
{
    [DisposeOnRemove]
    public TrecsList<int> Data;     // freed when the entity is removed
}
```

This is the declarative replacement for the manual
[`OnRemoved`-disposes-a-pointer pattern](../experimental/pointers.md#cleanup-is-manual-for-entity-owned-pointers).
You can still write that observer by hand when you need custom cleanup logic;
`[DisposeOnRemove]` covers the common case of "just dispose the field."

## Ordering guarantee

Removal handling runs in two phases, in this order:

1. **Reads** — every user `OnRemoved` callback **and** every `[CascadeRemove]`
   handler for the submission's removed entities.
2. **Disposes** — every `[DisposeOnRemove]` handler.

Because all reads happen strictly before any dispose, a callback that reads a
`[DisposeOnRemove]` field — directly, or across an `[CascadeRemove]`
`EntityHandle` into another removed entity — never observes freed storage. Your
`OnRemoved` handler can safely read the list it's about to have freed:

```csharp
World.Events
    .EntitiesWithTags<HolderTag>()
    .OnRemoved((group, range) =>
    {
        var buf = World.ComponentBuffer<Holder>(group).Read;
        for (int i = range.Start; i < range.End; i++)
        {
            // Still valid here: DisposeOnRemove for this field runs later.
            int count = buf[i].Data.Read(World).Count;
        }
    });
```

## Composing the two attributes

Mark the same `TrecsList<EntityHandle>` field with both attributes to *remove the
referenced entities* and *free the list's backing storage* on owner removal. The
ordering guarantee covers this composition: the field is read for the cascade
(phase 1) **before** its list is freed (phase 2).

```csharp
partial struct Owner : IEntityComponent
{
    [CascadeRemove, DisposeOnRemove]
    public TrecsList<EntityHandle> Children;
}
```

Removing an `Owner` entity removes all its `Children` entities and frees the
`Children` list in a single `Submit()`.

## Diagnostics

The source generator validates field types at compile time:

| Code | Fires when |
|---|---|
| **TRECS134** | A `[CascadeRemove]` field is not `EntityHandle` or `TrecsList<EntityHandle>`. |
| **TRECS135** | A `[DisposeOnRemove]` field is not one of `TrecsList<T>`, `UniquePtr<T>`, `SharedPtr<T>`, `NativeUniquePtr<T>`, `NativeSharedPtr<T>`. |

Both are errors — remove the attribute or change the field type to fix.

## Known limitation: cross-handle reads in whole-group `OnRemoved`

The phase-1-before-phase-2 (read-before-dispose) guarantee is scoped *within a
single removal path*. There is one rare gap to be aware of when a whole-group
`OnRemoved` callback reaches *across an `EntityHandle`* into another entity that
was **per-entity removed and already disposed** earlier in the same `Submit()`.
In that situation the callback could observe freed storage on the target entity.

This is extremely rare in practice, and the target is already `!Exists()` when it
happens — so the usual `EntityHandle.Exists` guard you'd apply before a
cross-entity read already protects you. Read a `[DisposeOnRemove]` field on an
entity *other than the one being removed* only after checking `Exists`.

## See also

- [Entity Events](entity-events.md) — the `OnRemoved` observer API these
  attributes build on, and the manual cleanup pattern they replace.
- [Pointers](../experimental/pointers.md#cleanup-is-manual-for-entity-owned-pointers)
  — manual disposal of entity-owned pointers, the longhand `[DisposeOnRemove]`
  automates.
- [Structural Changes](structural-changes.md) — when submission runs and how
  removal paths differ.

!!! info "Relationship to the broader cascade design"
    `[CascadeRemove]` is the **Destroy-only, forward-only minimal slice** of a
    larger relationship-lifetime feature (tracked internally as
    *"Relationship lifetime cascade"*). That broader design also envisions
    *Detach* and *Ignore* policies and an owner→children reverse index.
    `[CascadeRemove]` covers the common exclusive-ownership case today; the rest
    remains future work.
