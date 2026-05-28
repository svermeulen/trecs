# Best Practices

Recommended practices for building with Trecs.

## Systems

- **Declare system dependencies explicitly.** Use [`[ExecuteAfter]` / `[ExecuteBefore]`](../core/systems.md#system-ordering) instead of relying on registration order.
- **Pick the right [phase](../core/systems.md#update-phases).** `Fixed` for simulation, `Input` for queueing inputs, `EarlyPresentation` / `Presentation` / `LatePresentation` for rendering and transform sync. The phase determines which [Accessor Role](../advanced/accessor-roles.md) you get and what you're allowed to do in it.
- **Keep fixed-update objects stateless.** Includes systems and service classes used by fixed-update systems. Constructor parameters with immutable configuration are fine. Mutable state belongs in components or [heap pointers](../experimental/pointers.md), where it's serialized, deterministic, and visible to tooling. Otherwise state diverges between record and replay. Variable-update systems don't have this constraint (desyncs aren't possible there), though it's still convenient to use components to co-locate data with entities.

## Components

- **Data only, no logic.** Components are unmanaged structs with fields. Put logic in systems.
- **Keep components small and focused.** Prefer `Health { Current, Max }` over a 20-field `CharacterStats`. Many components often hold just a single field — use [`[Unwrap]`](../core/components.md#the-unwrap-shorthand) with Aspects to skip the outer struct.
- **Unmanaged only.** No classes, strings, arrays, or reference types. Use [heap pointers](../experimental/pointers.md) for managed/dynamically-sized data and [`FixedList<N>`](../advanced/fixed-collections.md) for inline lists.

## Entities & Templates

- **Templates describe design concepts.** `Bullet`, `Player`, `Enemy` — not `EntityWithHealthAndPosition`.

## General

- **Prefer `World.Rng`, not `UnityEngine.Random`.** External RNG breaks replay. `FixedRng` and `VariableRng` are independent streams.
- **Prefer `World.DeltaTime` / `World.ElapsedTime`,** not `UnityEngine.Time.deltaTime`, `UnityEngine.Time.time` or `DateTime` / `Stopwatch`

## See also

- [Gotchas](gotchas.md) — common mistakes, edge cases, and surprises with the symptom / cause / fix for each.
