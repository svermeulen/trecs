# Accessor Roles

Every `WorldAccessor` is tagged with an `AccessorRole` that controls what the accessor is allowed to do — which components it can read and write, whether it can make structural changes, whether it can allocate from the heap, and which RNG stream it can pull from. The role is set at accessor-creation time and never changes.

System-owned accessors get their role automatically from their `SystemPhase` (see [System-owned accessors](#system-owned-accessors-vs-standalone-accessors) below).  Non system classes can also create accessors via `world.CreateAccessor(AccessorRole, ...)` and pick the role explicitly.

The framework asserts every rule below at the call site. Crossing a role boundary throws an immediate, clear `TrecsException` rather than producing silent desync later.

## The three roles

- **`Fixed`** — owns the deterministic simulation. Reads and writes simulation state, and allocates persistent heap. Render-only state (anything marked `[VariableUpdateOnly]`) is off-limits. Default for `[ExecuteIn(SystemPhase.Fixed)]` systems, which is the implicit default for any `ISystem`.
- **`Variable`** — drives presentation. Reads simulation state to render it, and reads/writes the render-only `[VariableUpdateOnly]` state that goes with it. Can read deterministic state but cannot mutate it. Default for the three presentation phases and for input systems (input systems get a few extra permissions on top — see [Input System](../core/input-system.md)).
- **`Unrestricted`** — escape hatch for non-system code (lifecycle hooks, event callbacks, networking, debug tooling, scripting bridges). Bypasses all role rules.

## Capability matrix

| Capability | `Fixed` | `Variable` | `Unrestricted` |
|---|---|---|---|
| Read sim component (non-[`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template)) | ✅ | ✅ | ✅ |
| Write sim component (non-[`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template)) | ✅ | ❌ | ✅ |
| Read [`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template) component | ❌ | ✅ | ✅ |
| Write [`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template) component | ❌ | ✅ | ✅ |
| Persistent heap alloc (`AllocShared`, `AllocUnique`, native variants) | ✅ | ❌ | ✅ |
| Structural change (`AddEntity` / `RemoveEntity` / `MoveTo`) on a non-VUO template | ✅ | ❌ | ✅ |
| Structural change on a [`[VariableUpdateOnly]`](#vuo-field-vs-vuo-template) template | ❌ | ✅ | ✅ |
| Read set (`Set<T>().Read` — `Exists`, `Count`, iterate) | ✅ | ✅ | ✅ |
| Mutate set (`Set<T>().Defer`, `Set<T>().Write`) | ✅ | ❌ | ✅ |
| `SetSystemPaused` | ✅ | ❌ | ✅ |
| `FixedRng` | ✅ | ❌ | ✅ |
| `VariableRng` | ❌ | ✅ | ✅ |

### VUO field vs VUO template

`[VariableUpdateOnly]` can apply at two scopes, and they don't behave the same. The distinction matters when reading the structural-change rows of the matrix above.

- **`[VariableUpdateOnly]` on a component field** — the component is render-only state. `Fixed` accessors can't read or write it; `Variable` / input / `Unrestricted` can. **The structural-change rule is unaffected** — entities of the parent template are still simulation state and follow the normal rule (`Fixed` and `Unrestricted` create / remove / move them).

- **`[VariableUpdateOnly]` on a template class** — the entire template is render-cadence state (cameras, view-only helpers). The structural-change rule **inverts**: `Fixed` is rejected outright, and `Variable` / input / `Unrestricted` create / remove / move them. These groups are skipped from the determinism checksum.

See the [Components attribute reference](../core/components.md#component-field-attributes) for field-level usage and the [source-generator reference](source-generator-reference.md#components-and-templates) for both.

## Picking a role for a standalone accessor

Code outside the system model — services, scene initializers, editor inspectors, tests — creates its own accessor through `world.CreateAccessor(AccessorRole)`. Pick the role that matches what the accessor needs to do:

- **`Fixed`** — Service classes specifically for the deterministic game simulation. For example, a stats service that subscribes to `OnAdded` / `OnRemoved` for a tag and bumps a global score component on every spawn / despawn — see [Sample 18 — Reactive Events](../samples/18-reactive-events.md).
- **`Variable`** — UI code, camera controller, rendering services that read both sim and render state.
- **`Unrestricted`** — Scene initialization, debug menus, editor tooling.

## System-owned accessors vs standalone accessors

System-owned accessors map their `SystemPhase` to a role automatically:

| `SystemPhase` | `AccessorRole` |
|---|---|
| `Fixed` | `Fixed` |
| `Input` / `EarlyPresentation` / `Presentation` / `LatePresentation` | `Variable` |

The presentation phases and the input phase all collapse into the single `Variable` role because they share the same access rules — only their execution-order positions within the per-frame pipeline differ.

System code therefore never writes `world.CreateAccessor(AccessorRole.X, ...)` for itself — it gets the right role for free from its `[ExecuteIn(...)]` attribute. Use `CreateAccessor` only for the standalone cases listed in [Picking a role](#picking-a-role-for-a-standalone-accessor) above.

## Related

- [Heap Allocation Rules](heap-allocation-rules.md) — the heap-specific subset of these rules, plus deterministic ID minting, the seeder pattern, and the `OnRemoved` cleanup convention.
- [Input System](../core/input-system.md) — how `[Input]` components and `AddInput<T>` work with input systems.
- [Time & RNG](time-and-rng.md) — `FixedRng` vs `VariableRng` deterministic streams.
- [Disabling & Pausing Systems](system-control.md) — when to use `SetSystemPaused` vs `EnableChannel`.
