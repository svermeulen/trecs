# Design Rules

Opinionated rules for building with Trecs. Each one points at a longer reference page if you want the full story.

## Systems

- **Keep fixed-update systems stateless.** Constructor parameters for immutable configuration are fine; mutable state belongs in components, where it's serialized, deterministic, and visible to tooling. State on a system field diverges between record and replay.
- **Declare system dependencies explicitly.** Use [`[ExecuteAfter]` / `[ExecuteBefore]`](../core/systems.md#system-ordering) instead of relying on registration order.
- **Pick the right phase.** `Fixed` for simulation, `Input` for queueing inputs, `Presentation` / `LatePresentation` for rendering and transform sync. The phase determines which [Accessor Role](../advanced/accessor-roles.md) you get and what you're allowed to do in it.

## Components

- **Data only, no logic.** Components are unmanaged structs with fields. Put logic in systems.
- **Keep components small and focused.** Prefer `Health { Current, Max }` over a 20-field `CharacterStats`. Many components hold a single field — use [`[Unwrap]`](../core/components.md#the-unwrap-shorthand) so call sites read like the data, not the struct.
- **Unmanaged only.** No classes, strings, arrays, or reference types. Use [heap pointers](../advanced/heap.md) for managed/dynamically-sized data and [`FixedList<N>`](../advanced/fixed-collections.md) for inline lists.

## Entities & Templates

- **Templates should describe design concepts.** `Bullet`, `Player`, `Enemy` — not `EntityWithHealthAndPosition`.
- **No runtime composition changes.** A template's component set is fixed at compile time. The escape hatches:
    - **[Partitions](../core/templates.md#partitions)** — declared moves between tag combinations of the same template; component data is preserved across the move.
    - **Boolean / enum fields on a component** — the simplest option for "in state X, ignore field Y", but the unused fields still take memory in every state.
    - **[Sets](../entity-management/sets.md)** — sparse membership flags, independent of component storage.
    - **Child entity** — when the conditional shape needs *different* components, spawn a separate entity (possibly one of several templates) and reference it via an `EntityHandle` on a component of the parent.

## Structural changes & heap

- **Structural changes are deferred.** `AddEntity`, `RemoveEntity`, and `MoveTo` are queued and applied at the next submission, which runs at the end of each fixed step. A system later in the same tick won't see the new state — readers see the change on the next tick. If you need same-tick visibility, call `World.SubmitEntities()` manually between the writing and reading systems.
- **Honour the [accessor-role rules](../advanced/accessor-roles.md).** `Fixed` writes deterministic state and makes structural changes against non-VUO templates; `Variable` reads everything, writes `[VariableUpdateOnly]` components, and makes structural changes against `[VariableUpdateOnly]` templates only; `Unrestricted` is for non-system code (init, lifecycle hooks, editor tooling).
- **Allocate the heap from the right role.** Persistent heap (`SharedPtr` / `NativeSharedPtr`) is `Fixed`-only; frame-scoped heap is `Input`-only. See [Heap Allocation Rules](../advanced/heap-allocation-rules.md).

## Determinism

- **Use `World.Rng`, never `UnityEngine.Random`.** External RNG breaks replay. `FixedRng` and `VariableRng` are independent streams.
- **Use `world.FixedDeltaTime` in fixed-update systems.** `UnityEngine.Time.deltaTime` varies per render frame and breaks replay.
- **Provide sort keys in parallel structural-change jobs.** `NativeWorldAccessor` ops (`AddEntity` / `RemoveEntity` / `MoveTo` from a job) need a deterministic sort key for replay determinism.
- **Enable [`RequireDeterministicSubmission`](../entity-management/structural-changes.md#deterministic-submission)** for any project that records, replays, or networks state.

## Common anti-patterns

| Anti-Pattern | Problem | Solution |
|---|---|---|
| Logic in components | Breaks data/logic separation | Move to systems or util classes |
| Mutable fixed-system fields | Not serialized, diverges between record/replay | Store dynamic state in components |
| `UnityEngine.Random` / `Time.deltaTime` in fixed update | Breaks determinism | `World.Rng`, `world.FixedDeltaTime` |
| Tight coupling between systems | Fragile ordering, hidden dependencies | Explicit `[ExecuteAfter]` / `[ExecuteBefore]` |
| Allocating heap from `Variable` / `Presentation` | Fails the role's heap-allocation rule | Allocate in `Fixed` (or `Input` for frame-scoped) |
