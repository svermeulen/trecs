# FAQ

## Can I use Trecs alongside Unity's built-in ECS (DOTS)?

Yes — they're completely independent.  You could run both in the same project and then sync Trecs with UECS using an approach similar to [OOP Integration](oop-integration.md).

## Do I have to maintain determinism and serialization support to use Trecs?

No. Both are opt-in.

You can use `UnityEngine.Random`, `Time.deltaTime`, and store mutable state in system fields.  The simulation will run fine.  However, you should be aware of what you're giving up:

- **Record / replay / scrub** — the editor's rewind buffer and recording sessions rely on deterministic re-simulation. Non-deterministic systems make replays drift from the original run.
- **Snapshots** — save/load via the snapshot system requires that all mutable state lives in serializable components. State stored in system fields is invisible to snapshots and silently lost on restore.
- **Analyzer warnings** — the built-in analyzers (TRECS128–130) flag `UnityEngine.Random`, `Time.deltaTime`, `DateTime.Now`, `System.Random`, and `Dictionary`/`HashSet` iteration inside fixed-update systems. Since fixed update is the default phase, most of your systems will trigger these. You can suppress them per-method or project-wide with `#pragma warning disable TRECS130` (or similar), but consider whether the trade-off is worth it — re-enabling determinism later in a project is substantially harder than starting with it.
- **Testing** — deterministic simulations are straightforward to test headlessly (feed inputs, assert outcomes). Non-deterministic ones require tolerance ranges or mocking.

If you only need non-deterministic behavior in presentation code (rendering, audio, UI), put that logic in `[ExecuteIn(SystemPhase.Presentation)]` or `[ExecuteIn(SystemPhase.EarlyPresentation)]` systems — the analyzers don't fire outside of Fixed, and the separation keeps the simulation itself clean.

## What's the practical entity ceiling?

The included samples are exercised up to ~1M entities ([Sample 12 — Feeding Frenzy Benchmark](../samples/12-feeding-frenzy-benchmark.md) defaults to a max of 1M fish; [Sample 05 — Job System](../samples/05-job-system.md) defaults to 100k particles). Beyond that the bottleneck is usually rendering, not the ECS. Structure-of-arrays storage and contiguous iteration scale well past 100k with Burst-compiled jobs.

## What's the overhead of source generation?

All generators use Roslyn's `IIncrementalGenerator` API with value-based caching — each generator projects syntax into a plain `readonly record struct` model (no Roslyn symbols retained), and the pipeline short-circuits code emission entirely when the model hasn't changed. This means editing a file that doesn't affect any Trecs attribute typically costs zero generation work, and editing one that does only regenerates the affected outputs. In practice, you shouldn't notice any compile-time impact during normal development.

## Do I have to use `[WrapAsJob]` — can I still write manual jobs?

Yes. `[WrapAsJob]` is a convenience — it wraps a static method into a Burst job with `NativeWorldAccessor` wired up. For patterns the wrapper doesn't support (custom sort keys, non-Execute entry points, chained jobs), write an `IJobFor` / `IJobParallelFor` by hand and schedule it yourself. [Sample 05 — Job System](../samples/05-job-system.md) demonstrates both.

## Can I add or remove components from an entity at runtime?

No. Components are locked to the entity's template at creation. This is a deliberate design choice. Escape hatches:

- **[Partitions](../core/templates.md#partitions)** — declared moves between tag combinations of the same template; component data is preserved.
- **Boolean / enum fields on a component** — simplest for "in state X, ignore field Y", but unused fields still take memory in every state.
- **[Sets](../entity-management/sets.md)** — sparse membership flags, independent of component storage.
- **Child entity** — when the conditional shape needs *different* components, spawn a separate entity and reference it via an `EntityHandle` on a parent component.

## Can I use world snapshots as my save-game format?

Within a single build, yes — snapshots are fast, exact, and great for quicksaves, autosaves, and crash recovery. Across game patches, no: a snapshot is a raw image of your world schema, so any patch that changes a component, template, tag, or set invalidates existing files (loads fail up front with a schema-fingerprint error rather than corrupting). Games that need patch-durable saves should author their own domain-level save format — save the logical facts, rebuild the world through normal gameplay paths on load. See [Save games and game patches](../experimental/serialization.md#save-games-and-game-patches).

## Where do I report bugs / request features?

[GitHub Issues](https://github.com/svermeulen/trecs/issues) for bugs and concrete feature requests. For open-ended questions, design discussion, or sharing what you're building, use [GitHub Discussions](https://github.com/svermeulen/trecs/discussions).
