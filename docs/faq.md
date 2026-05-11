# FAQ

## Can I use Trecs alongside Unity's built-in ECS (DOTS)?

In principle yes — they're independent runtimes. In practice you'd run two ECS worlds, two sets of systems, two storage models, and two job-scheduling strategies in one project. Pick one if you can.

Trecs is aimed at **deterministic game simulation with recording/playback and network rollbacks**. Unity DOTS optimises for raw parallel throughput but offers no determinism guarantees or built-in serialization. If that's your project shape, Trecs fits; DOTS doesn't.

## What's the practical entity ceiling?

Samples have been exercised with 100k+ entities (see [Sample 05 — Job System](samples/05-job-system.md) and [Sample 12 — Feeding Frenzy Benchmark](samples/12-feeding-frenzy-benchmark.md)). Beyond that, the bottleneck is usually rendering, not the ECS. Structure-of-arrays storage and per-group iteration scale well past 100k with Burst-compiled jobs.

## Why doesn't my aspect compile when I forget `partial`?

The generator emits ref-returning property methods (e.g. `ref readonly Position Position`) as a partial declaration that merges with your hand-written aspect struct. Without `partial`, the generator's half is rejected and you get `TRECS009: Aspect must be partial`.

The same rule applies to `ITemplate` classes (`TRECS030`), `ISystem` classes (`TRECS040`), and source-generated jobs (`TRECS074`). If you see "method is not defined" on a member you know the generator emits, check that the containing type is `partial`.

## How do source-generated files show up in my IDE?

Files are produced at compile time under `obj/<Configuration>/<TargetFramework>/generated/`. Rider and Visual Studio show them under **Dependencies → Analyzers → Trecs.SourceGen**; VS Code's C# extension does the same once analyzers load. They're read-only — regenerated verbatim each compile.

If files *don't* show up after an edit, the analyzer may have crashed. Check the Unity console for `[ERROR]` lines tagged with a generator name, or look for a `TRECS###` diagnostic on the target type.

## What's the overhead of source generation?

The generator runs per compilation and caches via the Roslyn incremental pipeline. Steady-state runs (one file changed) are sub-100ms for most projects; the first compile after a solution reload is the slow one. Set the `SOURCEGEN_TIMING` environment variable for per-step timings.

## Do I have to use `[WrapAsJob]` — can I still write manual jobs?

Yes. `[WrapAsJob]` is a convenience — it wraps a static method into a Burst job with `NativeWorldAccessor` wired up. For patterns the wrapper doesn't support (custom sort keys, non-Execute entry points, chained jobs), write an `IJobFor` / `IJobParallelFor` by hand and schedule it yourself. [Sample 05 — Job System](samples/05-job-system.md) demonstrates both.

## Can I add or remove components from an entity at runtime?

No. Components are locked to the entity's template at creation. This is deliberate — it's what enables structure-of-arrays storage per group (rather than a sparse per-entity hash) and deterministic iteration order.

For "optional" behaviour, use:
- **Partitions** — move the entity between partitioned groups of the same template (see [Sample 06 — Partitions](samples/06-partitions.md)).
- **Sets** — a separate dynamic membership index (see [Sample 08 — Sets](samples/08-sets.md)).
- **Child entity** — spawn a separate entity that *has* the conditional components and references the parent by `EntityHandle`.

## How do I convert an existing OOP codebase to Trecs?

There's no automatic migration. The practical path:

1. Identify per-instance data across many instances (positions, health, etc.) — those become components.
2. Identify logic that operates on many instances — that becomes systems.
3. Identify "one of" managers (audio, save, UI root) — leave them as MonoBehaviours. Cross the boundary at the composition root.

See [OOP Integration](guides/oop-integration.md) for the boundary patterns.

## Is there a "debug draw" for entities?

The [Hierarchy window](editor-windows/hierarchy.md) shows every entity in every world with a JSON-editable component inspector. The [Trecs Player window](editor-windows/player.md) handles record / scrub / fork / loop — the right tool for "what does the simulation look like at frame N". For ad-hoc visual probes, a `MonoBehaviour` that queries the world from a variable-update system and calls `UnityEngine.Debug.Draw*` is usually enough.

## Where do I report bugs / request features?

[GitHub Issues](https://github.com/svermeulen/trecs/issues), using the appropriate template. For build/Unity issues, include the Unity editor version and `dotnet --version` output. For source-generator issues, include the diagnostic ID (`TRECS###`) if one was raised.
