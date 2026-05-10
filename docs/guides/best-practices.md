# Best Practices

Recommended practices for building with Trecs.  We assume here that determinism is required property which may not apply to all games.

## Systems

- **Declare system dependencies explicitly.** Use [`[ExecuteAfter]` / `[ExecuteBefore]`](../core/systems.md#system-ordering) instead of relying on registration order.
- **Pick the right [phase](../core/systems.md#update-phases).** `Fixed` for simulation, `Input` for queueing inputs, `Presentation` / `LatePresentation` for rendering and transform sync. The phase determines which [Accessor Role](../advanced/accessor-roles.md) you get and what you're allowed to do in it.
- **Keep fixed-update objects stateless.** This includes systems but also service classes used by fixed update systems. Constructor parameters for immutable configuration after initialization are fine. Mutable state belongs in components, where it's serialized, deterministic, and visible to tooling. Otherwise, state diverges between record and replay.  This applies less strongly to variable update systems, since desyncs aren't possible there, though it's still often best to keep state in components in those cases too (using [`[VariableUpdateOnly]`](../advanced/accessor-roles.md#vuo-field-vs-vuo-template) entities/components) for the same reasons.

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

## General

- **Prefer `World.Rng`, not `UnityEngine.Random`.** External RNG breaks replay. `FixedRng` and `VariableRng` are independent streams.
- **Prefer `World.DeltaTime` / `World.ElapsedTime`,** not `UnityEngine.Time.deltaTime`, `UnityEngine.Time.time` or `DateTime` / `Stopwatch`
- **Enable [`RequireDeterministicSubmission`](../entity-management/structural-changes.md#deterministic-submission)** for any project that records, replays, or networks state.

## See also

- [Gotchas](gotchas.md) — common mistakes, edge cases, and surprises with the symptom / cause / fix for each.
