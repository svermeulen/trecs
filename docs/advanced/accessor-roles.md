# Accessor Roles

Every `WorldAccessor` carries an `AccessorRole` that controls which components it can read/write, whether it can make structural changes, whether it can allocate heap, and which RNG stream it can pull from. The role is set at creation and never changes.

System-owned accessors derive their role from their `SystemPhase` (see [System-owned accessors](#system-owned-accessors-vs-standalone-accessors) below). Non-system code creates accessors via `world.CreateAccessor(AccessorRole)` and picks the role explicitly.

Every rule below is asserted at the call site. Crossing a role boundary throws an immediate `TrecsException` rather than producing silent desync later.

## The three roles

- **`Fixed`** — owns the deterministic simulation. Reads/writes simulation state and allocates persistent heap. Render-only state (`[VariableUpdateOnly]`) is off-limits. Default for `[ExecuteIn(SystemPhase.Fixed)]` systems — the implicit default for any `ISystem`.
- **`Variable`** — drives presentation. Reads simulation state and reads/writes the `[VariableUpdateOnly]` render state. Cannot mutate simulation state. Default for the three presentation phases and for input systems (input systems get extra permissions — see [Input System](../core/input-system.md)).
- **`Unrestricted`** — escape hatch for non-system code (lifecycle hooks, event callbacks, networking, debug tooling, scripting bridges). Bypasses all role rules.

## Capability matrix

| Capability | `Fixed` | `Variable` | `Unrestricted` |
|---|---|---|---|
| Read sim component (non-[`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template)) | ✅ | ✅ | ✅ |
| Write sim component (non-[`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template)) | ✅ | ❌ | ✅ |
| Read [`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template) component | ❌ | ✅ | ✅ |
| Write [`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template) component | ❌ | ✅ | ✅ |
| Persistent heap alloc (`AllocShared`, `AllocUnique`, native variants) | ✅ | ❌ | ✅ |
| Structural change (`AddEntity` / `RemoveEntity` / `SetTag` / `UnsetTag`) on a non-VUO template | ✅ | ❌ | ✅ |
| Structural change on a [`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template) template | ❌ | ✅ | ✅ |
| Read set (`Set<T>().Read` — `Exists`, `Count`, iterate) | ✅ | ✅ | ✅ |
| Mutate set (`Set<T>().DeferredAdd` / `DeferredRemove` / `DeferredClear`, `Set<T>().Write`) | ✅ | ❌ | ✅ |
| `SetSystemPaused` | ✅ | ❌ | ✅ |
| `FixedRng` | ✅ | ❌ | ✅ |
| `VariableRng` | ❌ | ✅ | ✅ |

### VUO field vs VUO template

`[VariableUpdateOnly]` applies at two scopes that behave differently. The distinction matters for the structural-change rows above.

- **`[VariableUpdateOnly]` on a component field** — the component is render-only state. `Fixed` cannot read or write it; `Variable` / input / `Unrestricted` can. **The structural-change rule is unaffected** — entities of the parent template are still simulation state, so `Fixed` and `Unrestricted` create / remove / partition-transition them.

- **`[VariableUpdateOnly]` on a template class** — the entire template is render-cadence state (cameras, view-only helpers). The structural-change rule **inverts**: `Fixed` is rejected; `Variable` / input / `Unrestricted` create / remove / partition-transition them. These groups are skipped from the determinism checksum.

See the [Components attribute reference](../core/components.md#component-field-attributes) for field-level usage.

## Picking a role for a standalone accessor

Non-system code (services, scene initializers, editor inspectors, tests) creates its own accessor via `world.CreateAccessor(AccessorRole)`. Pick the role that matches the work:

- **`Fixed`** — Service classes for the deterministic simulation. For example, a stats service that subscribes to `OnAdded` / `OnRemoved` for a tag and bumps a global score component — see [Sample 16 — Reactive Events](../samples/16-reactive-events.md).
- **`Variable`** — UI, camera controllers, rendering services that read both sim and render state.
- **`Unrestricted`** — Scene initialization, debug menus, editor tooling.

## System-owned accessors vs standalone accessors

System-owned accessors map their `SystemPhase` to a role automatically:

| `SystemPhase` | `AccessorRole` |
|---|---|
| `Fixed` | `Fixed` |
| `Input` / `EarlyPresentation` / `Presentation` / `LatePresentation` | `Variable` |

The presentation and input phases collapse into the single `Variable` role because they share the same access rules — only their [execution-order positions](../core/systems.md#phase-diagram) differ.

System code never calls `world.CreateAccessor(AccessorRole.X)` — it gets the role from its `[ExecuteIn(...)]` attribute. Use `CreateAccessor` only for the standalone cases listed above.

## Related

- [Shared Heap Data](shared-heap-data.md) — the heap-specific subset of these rules, plus deterministic ID minting and the seeder / provider patterns for shared blobs.
- [Input System](../core/input-system.md) — how `[Input]` components and `AddInput<T>` work with input systems.
- [Time & RNG](time-and-rng.md) — `FixedRng` vs `VariableRng` deterministic streams.
- [Pausing & Disabling Systems](pausing-and-disabling-systems.md) — when to use `SetSystemPaused` vs `EnableChannel`.
