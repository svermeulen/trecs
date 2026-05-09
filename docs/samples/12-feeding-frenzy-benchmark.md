# 12 — Feeding Frenzy Benchmark

A performance benchmark that implements the same gameplay using three different ECS architectures and nine iteration styles. Useful for understanding performance trade-offs.

**Source:** `Samples/12_FeedingFrenzyBenchmark/`

## What it does

The same fish-eating-meals simulation as [07 — Feeding Frenzy](07-feeding-frenzy.md), but implemented three different ways. You can switch approaches and iteration styles at runtime to compare performance with up to 200,000+ entities.

## Three approaches

### Branching

Eating/NotEating tracked via component values. Systems iterate all entities and branch on value:

```csharp
// All fish iterated, check if eating
if (fish.TargetMeal.IsNull)
{
    // Idle behavior
}
else
{
    // Eating behavior
}
```

**Trade-off:** Simple code, but iterates entities that don't need processing.

### Sets

Uses `IEntitySet` for membership tracking. Systems filter by set:

```csharp
[ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.NotEating))]
void IdleBob(in Fish fish) { ... }
```

**Trade-off:** Only visits relevant entities. No group changes when membership changes. Sparse iteration.

### Partitions

Uses `IHasPartition` for group separation:

```csharp
[ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
void IdleBob(in Fish fish) { ... }
```

**Trade-off:** Dense, cache-friendly iteration. But partition changes copy component data between groups.

## Nine iteration styles

Each approach is tested with multiple iteration patterns:

| Style | Description |
|-------|-------------|
| ForEachMethodAspect | `[ForEachEntity]` with aspect parameter |
| ForEachMethodComponents | `[ForEachEntity]` with component ref parameters |
| AspectQuery | Manual `Aspect.Query(World).WithTags<T>()` loop |
| QueryGroupSlices | `World.Query().GroupSlices()` with direct buffer access |
| RawComponentBuffersJob | Manual Burst job with `NativeComponentBufferRead/Write` |
| ForEachMethodAspectJob | `[ForEachEntity]` aspect with `[WrapAsJob]` |
| ForEachMethodComponentsJob | `[ForEachEntity]` components with `[WrapAsJob]` |
| WrapAsJobAspect | Static `[WrapAsJob]` method with aspect |
| WrapAsJobComponents | Static `[WrapAsJob]` method with components |

## Runtime controls

| Key | Action |
|-----|--------|
| F1 / F2 / F3 | Switch to Branching / Sets / Partitions |
| Tab / Shift+Tab | Cycle iteration style |
| Up / Down | Adjust entity count preset |

The display shows real-time performance stats: simulation Hz, FPS, entity count, and memory usage.

## Fish count presets

Logarithmically spaced from 5,000 to 1,000,000 entities for testing at different scales.

## Key implementation details

### Population management

Fish and meal counts smoothly lerp toward the desired preset. The spawning system prioritizes removing idle entities first to minimize disruption.

### GPU rendering

At high entity counts, the `RendererSystem` uses GPU-instanced indirect rendering. A Burst job marshals Position, Rotation, Scale, and Color from ECS components to GPU instance buffers each frame.

### Bidirectional entity references

Fish ↔ Meal pairing uses `EntityHandle` cross-references with cleanup handlers to prevent dangling references when either entity is removed.

## Concepts introduced

- **Partitions are fastest for dense iteration** — entities are contiguous in memory
- **Sets avoid group explosion** — no combinatorial blowup with multiple dimensions
- **Branching is simplest but slowest** — iterates everything, branches per entity
- **Jobs provide the biggest speedup** — Burst + parallel iteration scales with core count
- **Source generation produces efficient code** — `[ForEachEntity]` and `[WrapAsJob]` generate tight loops comparable to hand-written jobs
