# Design Rules

Best practices for building with Trecs.

## Systems

- **Keep systems stateless.** Use constructor parameters for configuration, but don't store mutable state in system fields. All mutable state belongs in components.
- **Each system does one thing.** A system that moves entities shouldn't also render them.
- **Handle 0-N entities.** Never assume a specific entity count — systems should work whether there are 0, 1, or 10,000 matching entities.
- **Declare dependencies explicitly.** Use `[ExecutesAfter]` and `[ExecutesBefore]` to make ordering requirements clear.
- **Don't communicate between systems via shared state.** Systems read and write components — that's how data flows between them.

## Components

- **Data only, no logic.** Components are structs with fields. Put logic in systems.
- **Keep components small and focused.** A `Health` component with `Current` and `Max` is better than a `CharacterStats` component with 20 fields.
- **Always add `[TypeId]`.** Required for serialization. Use a unique integer per component type.
- **Unmanaged only.** No classes, strings, arrays, or reference types. Use [heap pointers](../advanced/heap.md) for managed data.

## Entities & Templates

- **Templates define the game design language.** Name templates after game concepts: `BulletEntity`, `PlayerEntity`, `EnemyEntity`.
- **Use states for core lifecycle.** If an entity has 1-2 mutually exclusive modes (Alive/Dead, Active/Inactive), use `IHasState`. See [Entity Subset Patterns](entity-subset-patterns.md).
- **Avoid runtime composition changes.** Unlike some ECS frameworks, Trecs entities don't dynamically add/remove components. Design your templates to include all components the entity will ever need, and use states or sets to change behavior.
- **Use template inheritance for shared layouts.** If multiple entity types share Position, Rotation, and Scale, create a `Renderable` base template with `IExtends`.

## Tags & Groups

- **Use tags for classification.** Tags define what an entity *is*, not what it's *doing*.
- **Limit state dimensions to 2-3 max.** Beyond that, use sets to avoid group explosion.
- **Tags are free.** They carry no data and have no runtime cost beyond defining group membership.

## Structural Changes

- **All structural changes are deferred.** Never assume immediate effect from `AddEntity`, `RemoveEntity`, or `MoveTo`.
- **Prefer pre-allocation.** If you know how many entities you'll need, create them at initialization rather than spawning during simulation.

## Determinism

- **Use `World.Rng`, never `UnityEngine.Random`.** External RNG breaks replay.
- **Use sort keys in parallel jobs.** When using `NativeWorldAccessor` for structural changes in jobs, provide deterministic sort keys.
- **Enable `RequireDeterministicSubmission`** for any project that needs recording or networking.

## Common Anti-Patterns

| Anti-Pattern | Problem | Solution |
|---|---|---|
| Logic in components | Breaks data/logic separation | Move to systems |
| Mutable system fields | Non-deterministic, hard to reason about | Store state in components |
| `UnityEngine.Random` in ECS | Breaks determinism | Use `World.Rng` |
| Strings in components | Managed type, can't be in unmanaged struct | Use integer IDs or `[TypeId]` |
| Many state dimensions in templates | Group explosion (2^N groups) | Use sets |
| Tight coupling between systems | Fragile ordering, hidden dependencies | Explicit `[ExecutesAfter]`/`[ExecutesBefore]` |
