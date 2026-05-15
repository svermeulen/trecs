# FAQ

## Can I use Trecs alongside Unity's built-in ECS (DOTS)?

Yes — they're completely independent.  You could run both in the same project and then sync Trecs with UECS using an approach similar to [OOP Integration](guides/oop-integration.md).

## Do I have to maintain determinism and serialization support to use Trecs?

No.  

## What's the practical entity ceiling?

The included samples are exercised up to ~1M entities ([Sample 12 — Feeding Frenzy Benchmark](samples/12-feeding-frenzy-benchmark.md) defaults to a max of 1M fish; [Sample 05 — Job System](samples/05-job-system.md) defaults to 100k particles). Beyond that the bottleneck is usually rendering, not the ECS. Structure-of-arrays storage and per-group iteration scale well past 100k with Burst-compiled jobs.

## What's the overhead of source generation?

The generator runs per compilation and caches via the Roslyn incremental pipeline. Steady-state runs (one file changed) are sub-100ms for most projects; the first compile after a solution reload is the slow one. Set the `SOURCEGEN_TIMING` environment variable for per-step timings.

## Do I have to use `[WrapAsJob]` — can I still write manual jobs?

Yes. `[WrapAsJob]` is a convenience — it wraps a static method into a Burst job with `NativeWorldAccessor` wired up. For patterns the wrapper doesn't support (custom sort keys, non-Execute entry points, chained jobs), write an `IJobFor` / `IJobParallelFor` by hand and schedule it yourself. [Sample 05 — Job System](samples/05-job-system.md) demonstrates both.

## Can I add or remove components from an entity at runtime?

No. Components are locked to the entity's template at creation. This is a deliberate design choice — see [Best Practices: No runtime composition changes](guides/best-practices.md#entities-templates) for the escape hatches (partitions, boolean/enum fields, sets, child entities).

## Where do I report bugs / request features?

[GitHub Issues](https://github.com/svermeulen/trecs/issues) for bugs and concrete feature requests. For open-ended questions, design discussion, or sharing what you're building, use [GitHub Discussions](https://github.com/svermeulen/trecs/discussions).
