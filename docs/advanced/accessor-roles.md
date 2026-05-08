# Accessor Roles

Every `WorldAccessor` is tagged with an `AccessorRole` that controls what the accessor is allowed to do — which components it can read and write, whether it can make structural changes, whether it can allocate from the heap, and which RNG stream it can pull from. The role is set at accessor-creation time and never changes.

Roles are **permission tiers, not timing slots.** They don't say *when* the accessor runs (that's the system's `SystemPhase`); they say *what kind of work* the accessor is allowed to do. System-owned accessors get their role automatically from their `SystemPhase` (see [System-owned accessors](#system-owned-accessors-vs-standalone-accessors) below); standalone accessors created via `world.CreateAccessor(AccessorRole, ...)` pick the role explicitly.

The framework asserts every rule below at the call site. Crossing a role boundary throws an immediate, clear `AssertException` rather than producing silent desync later.

## The three roles

- **`Fixed`** — deterministic simulation. Writes simulation state, makes structural changes (`AddEntity` / `RemoveEntity` / `MoveTo`, set ops, `SetSystemPaused`), allocates persistent heap, pulls from `FixedRng`. Cannot read or write `[VariableUpdateOnly]` components. Default for systems with `[ExecuteIn(SystemPhase.Fixed)]` (the implicit default phase for any `ISystem`).
- **`Variable`** — display / render. Reads everything (including `[VariableUpdateOnly]`); writes `[VariableUpdateOnly]` only; pulls from `VariableRng`. No structural changes, no heap allocation. Default for any presentation-cadence system (`[ExecuteIn(SystemPhase.EarlyPresentation)]`, `[ExecuteIn(SystemPhase.Presentation)]`, `[ExecuteIn(SystemPhase.LatePresentation)]`) and for input systems (which additionally get input permissions — see below).
- **`Unrestricted`** — no role-specific restrictions. Bypasses every per-role rule (VUO read/write, structural changes, heap allocation, input ingestion, RNG stream selection). Intended for non-system code: lifecycle hooks (`Initialize` / `Dispose`), event callbacks, debug tooling, networking handlers, scripting bridges. **Use sparingly** — runtime code that runs as part of system execution should pick a real role so rule violations surface loudly instead of being silently swallowed.

## Input-system permissions

Input handling is *not* a role. The two input-specific permissions — calling `World.AddInput<T>(...)` and allocating frame-scoped heap (`AllocSharedFrameScoped`, etc.) — are auto-enabled on **system-owned accessors whose owning system is declared `[ExecuteIn(SystemPhase.Input)]`**. The role on those accessors is `Variable` (input systems share Variable's component-write rules), with the input-specific gates flipped on internally.

You can't manually create an accessor with input permissions via `world.CreateAccessor(AccessorRole, ...)` — that's deliberate. If you need to enqueue inputs from non-system code (e.g. a `MonoBehaviour` driver wrapping the world), use `AccessorRole.Unrestricted`, which is also allowed to call `AddInput<T>` and allocate frame-scoped heap.

## Capability matrix

The "Input system" column is a system-owned accessor with `[ExecuteIn(SystemPhase.Input)]` (role = `Variable` + input flag). Asterisks (`*`) and footnotes call out the cases that don't follow the obvious row pattern.

| Capability | Input system | `Fixed` | `Variable` | `Unrestricted` |
|---|---|---|---|---|
| Read sim component (non-`[VariableUpdateOnly]`) | ✅ | ✅ | ✅ | ✅ |
| Read `[VariableUpdateOnly]` component | ✅ | ❌ | ✅ | ✅ |
| Read `[Constant]` component | ✅ | ✅ | ✅ | ✅ |
| Write sim component (non-`[VariableUpdateOnly]`) | ❌ | ✅ | ❌ | ✅ |
| Write `[VariableUpdateOnly]` component | ✅ | ❌ | ✅ | ✅ |
| Write `[Constant]` component (post-creation) | ❌ | ❌ | ❌ | ❌ [^constant] |
| `AddInput<T>` | ✅ | ❌ | ❌ | ✅ [^none-input] |
| Persistent heap alloc (`AllocShared`, `AllocUnique`, native variants) | ❌ | ✅ | ❌ | ✅ |
| Frame-scoped heap alloc (`AllocSharedFrameScoped`, etc.) | ✅ | ❌ [^framescoped] | ❌ | ✅ |
| Structural change (`AddEntity` / `RemoveEntity` / `MoveTo`) | ❌ | ✅ | ❌ | ✅ |
| Set ops (`SetAdd` / `SetRemove`, `Set<T>().Write`) | ❌ | ✅ | ❌ | ✅ |
| `SetSystemPaused` | ❌ | ✅ | ❌ | ✅ |
| `FixedRng` | ❌ | ✅ | ❌ | ✅ |
| `VariableRng` | ✅ | ❌ | ✅ | ✅ |

[^constant]: `[Constant]` components are immutable after entity creation regardless of role. Init-time writes go through `EntityInitializer.SetRawImpl` at `AddEntity` time, which doesn't go through the role-checked write path. Even `Unrestricted` cannot rewrite a `[Constant]` component post-creation.

[^none-input]: `Unrestricted` is intentionally allowed to call `AddInput<T>` because the same rule (`AssertCanAddInputsSystem`) is the documented escape hatch for non-system code that needs to enqueue inputs (e.g. a `MonoBehaviour` driver wrapping `world.CreateAccessor(AccessorRole.Unrestricted, ...)`).

[^framescoped]: Frame-scoped heap allocation gates on `IsUnrestricted || IsInput` — the same predicate as `AddInput<T>` — so `Fixed` and `Variable` (without the input flag) accessors are both rejected. Frame-scoped pointers are an input-side mechanism for handing transient payloads into the simulation; from `Fixed`, allocate persistent (`AllocShared` / `AllocUnique`) instead.

## Picking a role for a standalone accessor

Code outside the system model — services, scene initializers, editor inspectors, tests — creates its own accessor through `world.CreateAccessor(AccessorRole, debugName)`. Pick the role that matches what the accessor needs to do:

- **`Fixed`** — service classes that own persistent heap allocations (the [seeder pattern](heap-allocation-rules.md#the-seeder-pattern-shared-assets-across-many-entities)), set up entities at init / dispose under sim-state rules, fire reactive observers, or write simulation state from a non-system entry point.
- **`Variable`** — UI code, debug inspectors, gizmo-rendering services that read both sim and render state but don't mutate either. The `VariableRng` accessor is also here.
- **`Unrestricted`** — non-system code that doesn't fit either tick-phase role: lifecycle hooks (`Initialize` / `Dispose`) that need to touch both sim and VUO state, event callbacks, networking handlers, scripting bridges, debug menus, editor tooling. Also the right pick for a `MonoBehaviour` driver that ingests player input and forwards via `AddInput<T>`. **Don't reach for `Unrestricted` to silence a rule violation in gameplay code that runs inside system execution** — that defeats the point of the rule. If a rule keeps tripping inside a system, the underlying design is probably wrong (typically: the work is being done from the wrong phase, or the data is on the wrong component).

> The same advice applies inside system implementations that hold a separate accessor for a service. Pass the system's own `WorldAccessor` down to the service rather than constructing a `Unrestricted` one — see [Strict-accessor-during-Fixed-execute rule](#strict-accessor-during-fixed-execute-rule) below.

## System-owned accessors vs standalone accessors

System-owned accessors map their `SystemPhase` to a role (and the input flag) automatically:

| `SystemPhase` | `AccessorRole` | Input flag |
|---|---|---|
| `Input` | `Variable` | ✅ |
| `Fixed` | `Fixed` | — |
| `EarlyPresentation` / `Presentation` / `LatePresentation` | `Variable` | — |

The three presentation phases collapse into the single `Variable` role because they share the same access rules — only their execution-order positions within the per-frame pipeline differ, which is a `SystemPhase` concern not an accessor concern. Input systems share `Variable`'s component-write rules and add the input-specific permissions on top via the auto-derived flag.

System code therefore never writes `world.CreateAccessor(AccessorRole.X, ...)` for itself — it gets the right role + flag for free from its `[ExecuteIn(...)]` attribute. Use `CreateAccessor` only for the standalone cases listed in [Picking a role](#picking-a-role-for-a-standalone-accessor) above.

## Strict-accessor-during-Fixed-execute rule

> **During a `Fixed`-role system's `Execute`, only that system's own accessor is allowed to touch ECS state.** Other accessors — even `Fixed`-role ones held by services, even `Unrestricted` accessors — throw if they're used mid-Fixed-execute.

The rule fires regardless of role: the assertion compares the accessor's `Id` against the currently-executing system's accessor `Id`. A service holding its own `AccessorRole.Fixed` accessor with a different `Id` is rejected the same as a `Unrestricted` accessor would be.

Why: even when the data being touched is deterministic, recording access under the service's `DebugName` instead of the calling system's scrambles debug attribution and tooling (profiler spans, dependency tracking, reactive-event accounting). And `Unrestricted` accessors smuggle non-deterministic state in besides.

The fix is to pass the calling system's `WorldAccessor` down to services rather than holding a separate one:

```csharp
// ❌ Service holds its own accessor; trips the strict-accessor rule
//    when called from a Fixed system's Execute.
class PaletteService
{
    WorldAccessor _world;

    public PaletteService(World world)
    {
        _world = world.CreateAccessor(AccessorRole.Fixed, "PaletteService");
    }

    public SharedPtr<ColorPalette> GetWarm() => _world.Heap.AllocShared<ColorPalette>(AssetIds.WarmPalette);
}

// ✅ Service takes the accessor in; the calling Fixed system passes its own.
class PaletteService
{
    public SharedPtr<ColorPalette> GetWarm(WorldAccessor world) =>
        world.Heap.AllocShared<ColorPalette>(AssetIds.WarmPalette);
}
```

**Variable-cadence phases don't enforce this rule** — services may freely use their own accessors during `EarlyPresentation` / `Presentation` / `LatePresentation` since none of those phases have determinism guarantees a service could break.

**Observer callbacks (`OnAdded` / `OnRemoved` / `OnMoved`) also don't enforce this rule** — they fire from inside `SubmitEntities`, which runs *between* Fixed-system executes rather than inside one. Service-class accessors are therefore valid in callbacks, which is what enables patterns like the [pointer-cleanup sample](../samples/10-pointers.md). See [Cascading structural changes from callbacks](../entity-management/entity-events.md#cascading-structural-changes-from-callbacks) for the full callback-cascade contract.

## Related

- [Heap Allocation Rules](heap-allocation-rules.md) — the heap-specific subset of these rules, plus deterministic ID minting, the seeder pattern, and the `OnRemoved` cleanup convention.
- [Input System](input-system.md) — how `[Input]` components and `AddInput<T>` work with input systems.
- [Time & RNG](time-and-rng.md) — `FixedRng` vs `VariableRng` deterministic streams.
- [Disabling & Pausing Systems](system-control.md) — when to use `SetSystemPaused` vs `EnableChannel`.
