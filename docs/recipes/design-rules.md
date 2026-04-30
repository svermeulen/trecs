# Design Rules

Best practices for building with Trecs.

## Systems

- **Keep fixed update systems stateless.** Use constructor parameters for immutable configuration, but don't store mutable state in system fields. All mutable state belongs in components.
- **Declare system dependencies explicitly.** Use [`[ExecuteAfter]` and `[ExecuteBefore]`](../core/systems.md#system-ordering) to make ordering requirements clear.

## Components

- **Data only, no logic.** Components are structs with fields. Put logic in systems.
- **Keep components small and focused.** A `Health` component with `Current` and `Max` is better than a `CharacterStats` component with 20 fields.  It's common and normal to have many components with just one field with [`[Unwrap]`](../core/components.md#the-unwrap-shorthand) attribute.
- **Unmanaged only.** No classes, strings, arrays, or reference types. Use [heap pointers](../advanced/heap.md) for managed data.

## Entities & Templates

- **Templates define the game design language.** Name templates after game concepts: `Bullet`, `Player`, `Enemy`.
- **Avoid designs that require runtime composition changes.** Unlike some ECS frameworks, Trecs entities don't dynamically add/remove components. Design your templates to include all components the entity will ever need, or move conditional state to a child entity

## Structural Changes

- **All structural changes are deferred.** Never assume immediate effect from `AddEntity`, `RemoveEntity`, or `MoveTo`.

## Determinism

- **Use `World.Rng`, never `UnityEngine.Random`.** External RNG breaks replay.
- **Use sort keys in parallel jobs.** When using `NativeWorldAccessor` for structural changes in jobs, provide deterministic sort keys.
- **Enable [`RequireDeterministicSubmission`](../entity-management/structural-changes.md#deterministic-submission)** for any project that needs recording or networking.

## Common Anti-Patterns

| Anti-Pattern | Problem | Solution |
|---|---|---|
| Logic in components | Breaks data/logic separation | Move to systems or util classes |
| Mutable fixed system fields | Non-deterministic, not serialized, inconsistent | Store all dynamic state in components |
| `UnityEngine.Random` in ECS | Breaks determinism | Use `World.Rng`.  |
| Tight coupling between systems | Fragile ordering, hidden dependencies | Explicit `[ExecuteAfter]`/`[ExecuteBefore]` |
