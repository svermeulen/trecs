# Design Rules

Opinionated rules for building with Trecs. Each one points at a longer reference page if you want the full story.

## Systems

- **Keep fixed-update systems stateless.** Constructor parameters for immutable configuration are fine; mutable state belongs in components, where it's serialized, deterministic, and visible to tooling. State on a system field diverges between record and replay.
- **Declare system dependencies explicitly.** Use [`[ExecuteAfter]` / `[ExecuteBefore]`](../core/systems.md#system-ordering) instead of relying on registration order.
- **Pick the right phase.** `Fixed` for simulation, `Input` for queueing inputs, `Presentation` / `LatePresentation` for rendering and transform sync. The phase determines which [Accessor Role](../advanced/accessor-roles.md) you get and what you're allowed to do in it.

## Components

- **Data only, no logic.** Components are unmanaged structs with fields. Put logic in systems.
- **Keep components small and focused.** Prefer `Health { Current, Max }` over a 20-field `CharacterStats`. Many components hold a single field — use [`[Unwrap]`](../core/components.md#the-unwrap-shorthand) so call sites read like the data, not the struct.
- **Unmanaged only.** No classes, strings, arrays, or reference types. Use [heap pointers](../advanced/heap.md) for managed data and [`FixedList<N>`](../advanced/fixed-collections.md) for inline lists.

## Entities & Templates

- **Templates name design concepts.** `Bullet`, `Player`, `Enemy` — not `EntityWithHealthAndPosition`.
- **No runtime composition changes.** A template's component set is fixed at compile time; entities don't gain or lose components at runtime. The escape hatches are [partitions](../core/templates.md#partitions) (declared moves between groups), boolean fields, and [sets](../entity-management/sets.md).

## Structural Changes & Heap

- **Structural changes are deferred.** `AddEntity`, `RemoveEntity`, and `MoveTo` don't take effect until the next submission. Order systems so writers run before readers within the tick, or accept a one-frame lag.
- **Honour the [accessor-role rules](../advanced/accessor-roles.md).** `Fixed` writes deterministic state and makes structural changes; `Variable` reads everything but only writes `[VariableUpdateOnly]`; `Unrestricted` is for non-system code (init, lifecycle hooks, editor tooling).
- **Allocate the heap from the right role.** Persistent heap (`SharedPtr` / `NativeSharedPtr`) is `Fixed`-only; frame-scoped heap is `Input`-only. See [Heap Allocation Rules](../advanced/heap-allocation-rules.md).

## Determinism

- **Use `World.Rng`, never `UnityEngine.Random`.** External RNG breaks replay. `FixedRng` and `VariableRng` are independent streams.
- **Use `world.FixedDeltaTime` in fixed-update systems.** `UnityEngine.Time.deltaTime` varies per render frame and breaks replay.
- **Provide sort keys in parallel structural-change jobs.** `NativeWorldAccessor` ops (`AddEntity` / `RemoveEntity` / `MoveTo` from a job) need a deterministic sort key for replay determinism.
- **Enable [`RequireDeterministicSubmission`](../entity-management/structural-changes.md#deterministic-submission)** for any project that records, replays, or networks state.

## Common Anti-Patterns

| Anti-Pattern | Problem | Solution |
|---|---|---|
| Logic in components | Breaks data/logic separation | Move to systems or util classes |
| Mutable fixed-system fields | Not serialized, diverges between record/replay | Store dynamic state in components |
| `UnityEngine.Random` / `Time.deltaTime` in fixed update | Breaks determinism | `World.Rng`, `world.FixedDeltaTime` |
| Tight coupling between systems | Fragile ordering, hidden dependencies | Explicit `[ExecuteAfter]` / `[ExecuteBefore]` |
| Allocating heap from `Variable` / `Presentation` | Fails the role's heap-allocation rule | Allocate in `Fixed` (or `Input` for frame-scoped) |
