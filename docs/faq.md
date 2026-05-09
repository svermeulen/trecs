# FAQ

## Can I use Trecs alongside Unity's built-in ECS (DOTS)?

In principle yes — they're independent runtimes. In practice you'd be running two ECS worlds, two sets of systems, two storage models, and two job-scheduling strategies in the same project. If you can commit to one, you'll have a simpler time.

The ideal use case for Trecs is a **deterministic game simulation with recording/playback and network rollbacks**. Unity DOTS optimises for raw parallel throughput but doesn't give you determinism guarantees or a serialization story out of the box. If that's the shape of your project, Trecs stays on the path you want; DOTS doesn't.

## What's the practical entity ceiling?

Trecs has been exercised in samples with 100k+ entities (see [Sample 05 — Job System](samples/05-job-system.md) and [Sample 12 — Feeding Frenzy Benchmark](samples/12-feeding-frenzy-benchmark.md)). Beyond that, the bottleneck is usually the rendering you're driving off entity data, not the ECS itself. Structure-of-arrays storage and per-group iteration scale well past 100k with Burst-compiled jobs.

## Why doesn't my aspect compile when I forget `partial`?

The source generator emits the ref-returning property methods for an aspect (e.g. `ref readonly Position Position`) as a partial declaration that merges with your hand-written aspect struct. If your struct is not declared `partial`, the generator's half is rejected by the compiler and you get diagnostic `TRECS009: Aspect must be partial`.

Same rule applies to `ITemplate` classes (`TRECS030`), `ISystem` classes (`TRECS040`), and source-generated jobs (`TRECS074`). If you see "method is not defined" on a member you know the generator emits, check that the containing type is `partial`.

## How do source-generated files show up in my IDE?

The files are produced at compile time and live under `obj/<Configuration>/<TargetFramework>/generated/`. In Rider and Visual Studio they appear in the solution tree under **Dependencies → Analyzers → Trecs.SourceGen**. VS Code's C# extension surfaces them similarly once analyzers are loaded. The generated files are read-only — they come back verbatim each compile.

If the files *don't* show up after an edit, the analyzer may have crashed. Check the Unity console for `[ERROR]` lines tagged with a generator name, or look for a `TRECS###` diagnostic on the type that should have been generated for.

## What's the overhead of source generation?

The generator runs per compilation and caches results through the Roslyn incremental pipeline. In a steady state (one file changed) each run is sub-100ms for most projects; the first compile after a solution reload is the slow one. Use `SOURCEGEN_TIMING` (documented in [Debugging](recipes/debugging.md#looking-at-generated-source)) to get per-step timings in the console.

## Do I have to use `[WrapAsJob]` — can I still write manual jobs?

Yes. `[WrapAsJob]` is a convenience — the generator wraps a static method into a proper Burst job with `NativeWorldAccessor` wired up. If you need job patterns the wrapper doesn't support (custom sort keys, non-Execute entry points, chained jobs), write a regular `IJobFor` / `IJobParallelFor` by hand and schedule it yourself. [Sample 05 — Job System](samples/05-job-system.md) demonstrates both flavours.

## Can I add or remove components from an entity at runtime?

No. Components are locked to the entity's template at creation time. This is a deliberate design choice — it's what lets the storage be a structure of contiguous arrays per group rather than a sparse hash per entity, and what makes iteration order deterministic.

If you need "optional" behaviour, the typical patterns are:
- **Partitions** — move the entity between two partitioned groups of the same template (see [Sample 06 — Partitions](samples/06-partitions.md)).
- **Sets** — a separate dynamic membership index (see [Sample 08 — Sets](samples/08-sets.md)).
- **Child entity** — spawn a separate entity that *has* the conditional components and references the parent by `EntityHandle`.

## How do I convert an existing OOP codebase to Trecs?

There's no automatic migration. The practical path is:

1. Identify the data that needs to be per-instance across many instances (positions, health, etc.) — those become components.
2. Identify the logic that operates on many instances — that becomes systems.
3. Identify the "one of" managers that should stay OOP (audio manager, save manager, UI root) — those stay as MonoBehaviours. Cross the boundary via the composition root.

See [OOP Integration](recipes/oop-integration.md) for the boundary-crossing patterns.

## Is there a "debug draw" for entities?

The [Hierarchy window](editor-windows/hierarchy.md) gives you a tree of every entity in every world along with a JSON-editable component inspector. The [Trecs Player window](editor-windows/player.md) handles record / scrub / fork / loop and is the right tool for "what does the simulation look like at frame N". For ad-hoc visual probes, a `MonoBehaviour` that queries the world from a variable-update system and calls `UnityEngine.Debug.Draw*` is usually enough.

## Where do I report bugs / request features?

[GitHub Issues](https://github.com/svermeulen/trecs/issues), using the appropriate template. For build/Unity-specific problems, include the Unity editor version and the output of `dotnet --version`. For source-generator issues, include the diagnostic ID (`TRECS###`) if one was raised.
