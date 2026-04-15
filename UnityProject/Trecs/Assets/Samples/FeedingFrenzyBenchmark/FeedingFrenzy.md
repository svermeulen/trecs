# FeedingFrenzy Benchmark

This sample benchmarks the performance of different ECS patterns in Trecs. It varies two independent axes -- **state approach** (how entities are partitioned by behavioral state) and **iteration style** (how systems loop over entities) -- across a fish-eating simulation with thousands of entities.

## The Simulation

- Fish entities and meal entities, with configurable counts (default: 5,000 fish + 3,500 meals)
- Fish spawn as NotEating, get paired with an available meal, move toward it (Eating), consume it, then return to NotEating
- At steady state a large fraction of fish are in the Eating state at any given time
- Four gameplay systems run each fixed tick: LookingForMeal, ConsumingMeal, Movement, IdleBob
- Only Eating fish need velocity computation and movement; only NotEating fish need meal pairing

The benchmark tests every combination of state approach and iteration style, producing 21 configurations. Each is run for 10 seconds with profiling enabled, then the results are aggregated.

## State Approaches

The three state approaches control how entities are organized in memory based on their behavioral state (Eating vs NotEating).

### States

Each entity template declares explicit states via `IHasState<NotEating>` and `IHasState<Eating>`. Trecs creates a **separate group** (and therefore separate contiguous component arrays) for each state. Systems query specific state groups directly:

```
MovementSystem queries (Fish, Eating)     -> iterates a dense, contiguous array of only eating fish
LookingForMeal queries (Fish, NotEating)  -> iterates a dense, contiguous array of only idle fish
```

State transitions use `MoveTo`, which copies the entity's component data from one group's arrays to another during `SubmitEntities`.

**Memory access**: Sequential over only the relevant subset. Optimal cache behavior.

**Trade-off**: Pays a submission cost for `MoveTo` operations (copying component data between groups on every state transition).

### Sets

Single group per template. Two explicit entity sets (`FrenzySets.Eating` and `FrenzySets.NotEating`) track which entities are in each state. Systems filter iteration to a specific set:

```
MovementSystem queries Fish, filtered to Eating set  -> iterates only eating fish
LookingForMeal queries Fish, filtered to NotEating set -> iterates only idle fish
```

State transitions add/remove entities from sets. The underlying component arrays are shared across all states.

**Memory access**: Index-indirected. The set provides a list of indices into the shared component arrays. Access is not fully sequential since set indices lose natural ordering through swap-back operations, but locality is better than branching since only relevant entities are touched.

**Trade-off**: Avoids the `MoveTo` copy cost of States, but iteration has index indirection overhead and weaker cache locality.

### Branching

Single group per template. State is implicit in component data (e.g., `TargetMeal` being null means NotEating). All systems iterate **every entity** and branch:

```
MovementSystem iterates ALL fish:
  for each fish:
    if (targetMeal.IsNull) continue;  // skip non-eating fish
    apply velocity
```

State transitions are simple field writes.

**Memory access**: Sequential over all entities, but wasteful -- reads cache lines for entities that will be skipped.

**Trade-off**: Zero structural overhead (no moves, no set tracking), but every system pays the cost of iterating the full entity count regardless of how many are relevant.

## Iteration Styles

The seven iteration styles control how system code loops over entities. They fall into two tiers: **main-thread** styles and **job** styles.

### Main-Thread Styles

These run synchronously on the main thread. They differ in abstraction level but all execute single-threaded without Burst compilation.

**ForEachMethodAspect** -- Source-generated loop using an aspect (a struct grouping related components):
```csharp
[ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish) })]
void Execute(in Fish fish)
{
    fish.Position += World.DeltaTime * fish.Velocity;
}
```
The framework generates the iteration loop. Components are accessed through the aspect's properties.

**ForEachMethodComponents** -- Source-generated loop with individual component parameters:
```csharp
[ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish) })]
void Execute(in Velocity velocity, ref Position position)
{
    position.Value += World.DeltaTime * velocity.Value;
}
```
Same generated loop, but components are passed as separate parameters rather than grouped into an aspect.

**AspectQuery** -- Manual iteration using the Query API with aspects:
```csharp
foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>())
{
    fish.Position += World.DeltaTime * fish.Velocity;
}
```
You control the loop. The framework provides the enumerator.

**QueryGroupSlices** -- Manual iteration over raw component arrays, grouped by archetype:
```csharp
foreach (var slice in World.Query().WithTags<FrenzyTags.Fish>().GroupSlices())
{
    var velocities = World.ComponentBuffer<Velocity>(slice.Group).Read;
    var positions = World.ComponentBuffer<Position>(slice.Group).Write;

    for (int i = 0; i < slice.Count; i++)
    {
        positions[i].Value += World.DeltaTime * velocities[i].Value;
    }
}
```
Lowest abstraction level on the main thread. You work with raw arrays in a tight inner loop with maximum data locality.

### Job Styles

These schedule work through Unity's job system with `[BurstCompile]`, which compiles the inner loop to native code via LLVM and can parallelize across CPU cores.

**RawComponentBuffersJob** -- Burst-compiled job with explicit buffer declarations:
```csharp
[BurstCompile]
partial struct MoveJob
{
    public float DeltaTime;

    [FromWorld(Tag = typeof(FrenzyTags.Fish))]
    public NativeComponentBufferRead<Velocity> Velocities;

    [FromWorld(Tag = typeof(FrenzyTags.Fish))]
    public NativeComponentBufferWrite<Position> Positions;

    public void Execute(int i)
    {
        Positions[i] = new Position { Value = Positions[i].Value + DeltaTime * Velocities[i].Value };
    }
}
```
Maximum control. You declare each buffer explicitly and index into them.

**ForEachMethodAspectJob** -- Burst-compiled job using aspects:
```csharp
[BurstCompile]
partial struct MoveJob
{
    public float DeltaTime;

    [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
    public readonly void Execute(in Fish fish)
    {
        fish.Position += DeltaTime * fish.Velocity;
    }
}
```
Same ergonomics as the main-thread aspect style, but Burst-compiled and parallelized.

**ForEachMethodComponentsJob** -- Burst-compiled job with component parameters:
```csharp
[BurstCompile]
partial struct MoveJob
{
    public float DeltaTime;

    [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
    public readonly void Execute(in Velocity velocity, ref Position position)
    {
        position.Value += DeltaTime * velocity.Value;
    }
}
```
Same as above but with individual component parameters.

## Results

All times in milliseconds, averaged per fixed tick. Profiled with 5,000 fish + 3,500 meals (8,500 total entities).

### Full Results (sorted by FixedTick)

| Config | FixedTick | Consuming | LookForMeal | Movement | Submit |
|--------|-----------|-----------|-------------|----------|--------|
| States + RawComponentBuffersJob | **0.20** | 0.017 | 0.012 | 0.011 | 0.069 |
| States + ForEachMethodAspectJob | **0.24** | 0.018 | 0.012 | 0.011 | 0.077 |
| States + ForEachMethodComponentsJob | **0.24** | 0.018 | 0.014 | 0.012 | 0.082 |
| Sets + RawComponentBuffersJob | **0.31** | 0.023 | 0.024 | 0.015 | 0.092 |
| Branching + RawComponentBuffersJob | **0.71** | 0.018 | 0.532 | 0.013 | 0.028 |
| Branching + ForEachMethodComponentsJob | **0.73** | 0.015 | 0.485 | 0.011 | 0.074 |
| Branching + ForEachMethodAspectJob | **0.73** | 0.018 | 0.497 | 0.013 | 0.042 |
| Sets + ForEachMethodComponentsJob | **0.87** | 0.278 | 0.018 | 0.258 | 0.063 |
| Sets + ForEachMethodAspectJob | **0.95** | 0.300 | 0.019 | 0.278 | 0.064 |
| States + QueryGroupSlices | **3.04** | 1.129 | 0.027 | 1.140 | 0.062 |
| States + ForEachMethodComponents | **3.37** | 1.340 | 0.032 | 1.274 | 0.068 |
| Sets + QueryGroupSlices | **3.44** | 1.283 | 0.039 | 1.269 | 0.083 |
| Sets + ForEachMethodComponents | **3.97** | 1.529 | 0.039 | 1.578 | 0.063 |
| States + ForEachMethodAspect | **4.47** | 1.863 | 0.033 | 1.551 | 0.067 |
| Branching + QueryGroupSlices | **4.62** | 1.500 | 0.494 | 1.525 | 0.043 |
| States + AspectQuery | **4.83** | 2.117 | 0.030 | 1.664 | 0.061 |
| Sets + ForEachMethodAspect | **5.15** | 2.179 | 0.039 | 1.794 | 0.080 |
| Sets + AspectQuery | **5.52** | 2.292 | 0.039 | 2.013 | 0.069 |
| Branching + ForEachMethodComponents | **6.66** | 1.731 | 1.557 | 1.900 | 0.043 |
| Branching + AspectQuery | **9.54** | 2.822 | 1.532 | 2.864 | 0.050 |
| Branching + ForEachMethodAspect | **9.60** | 2.805 | 1.621 | 2.843 | 0.031 |

### By State Approach (averaged across iteration styles)

| Approach | FixedTick | Consuming | LookForMeal | Movement | Submit |
|----------|-----------|-----------|-------------|----------|--------|
| States | 2.34 | 0.93 | 0.02 | 0.81 | 0.07 |
| Sets | 2.89 | 1.13 | 0.03 | 1.03 | 0.07 |
| Branching | 4.66 | 1.27 | 0.96 | 1.31 | 0.04 |

### By Iteration Style (averaged across state approaches)

| Iteration Style | FixedTick | Consuming | LookForMeal | Movement | Submit |
|-----------------|-----------|-----------|-------------|----------|--------|
| RawComponentBuffersJob | 0.41 | 0.02 | 0.19 | 0.01 | 0.06 |
| ForEachMethodComponentsJob | 0.61 | 0.10 | 0.17 | 0.09 | 0.07 |
| ForEachMethodAspectJob | 0.64 | 0.11 | 0.18 | 0.10 | 0.06 |
| QueryGroupSlices | 3.70 | 1.30 | 0.19 | 1.31 | 0.06 |
| ForEachMethodComponents | 4.67 | 1.53 | 0.54 | 1.58 | 0.06 |
| ForEachMethodAspect | 6.40 | 2.28 | 0.56 | 2.06 | 0.06 |
| AspectQuery | 6.63 | 2.41 | 0.53 | 2.18 | 0.06 |

## Analysis

### Jobs vs main-thread: 10-16x difference

The most dramatic result is the gap between job and non-job iteration styles. The three job styles (0.4-0.6ms) are **10-16x faster** than the four main-thread styles (3.7-6.6ms). This comes from two factors:

1. **Burst compilation**: Unity's Burst compiler converts the inner loop to native machine code via LLVM. This eliminates managed overhead (bounds checks, GC tracking, virtual dispatch) and enables SIMD vectorization. For tight arithmetic loops like position updates, Burst can be an order of magnitude faster than equivalent managed C# code.

2. **Multi-threading**: Jobs scheduled with `ScheduleParallel` distribute work across CPU cores. The entity batches are independent, so parallelism is nearly linear with core count for large enough workloads.

These two factors compound. Even at this relatively modest entity count (8,500), the combined effect produces a massive performance gap.

### Within jobs: RawComponentBuffers has a slight edge

Among the three job styles, RawComponentBuffersJob (0.41ms) is slightly faster than ForEachMethodComponentsJob (0.61ms) and ForEachMethodAspectJob (0.64ms). The difference is small because all three are Burst-compiled to native code, but RawComponentBuffersJob gives the compiler maximum visibility into the memory access pattern -- explicit buffer declarations with direct indexing, no abstraction layers in the way. In practice, the ForEachMethod job variants are close enough that the ergonomic benefit of aspects or component parameters is worth the marginal cost.

### Within main-thread styles: abstraction level matters

On the main thread, where there's no Burst compilation to flatten abstraction overhead, the differences between styles become visible:

- **QueryGroupSlices** (3.7ms) is fastest because it works with raw component arrays in a tight inner loop. The CPU prefetcher can predict the sequential access pattern, and there's minimal per-entity overhead.

- **ForEachMethodComponents** (4.7ms) adds per-entity dispatch overhead from the source-generated loop but still passes components as simple value parameters.

- **ForEachMethodAspect** (6.4ms) and **AspectQuery** (6.6ms) are the slowest. Aspects add a layer of indirection -- each property access on the aspect resolves to a component buffer lookup. Without Burst to inline and optimize these away, the overhead accumulates across thousands of entities.

The lesson: on the main thread, less abstraction means better performance. In jobs, Burst compilation eliminates most of this overhead, making the higher-abstraction styles nearly as fast as raw buffers.

### States vs Sets vs Branching

**States is fastest overall** (2.34ms avg) because entities in each state live in their own contiguous arrays. When MovementSystem queries `(Fish, Eating)`, it iterates a dense array containing only eating fish -- sequential memory access over exactly the entities it needs. This is optimal for both the CPU cache and the prefetcher.

**Sets is close behind** (2.89ms avg). Sets filter iteration to only relevant entities (like States), avoiding the wasted work of Branching. But the filtered indices point into shared component arrays, introducing scattered memory access. The indirection overhead is visible in Consuming and Movement, which are ~20% slower than States for the same iteration style.

**Branching is slowest** (4.66ms avg) primarily because of LookingForMeal. This system only needs NotEating fish, but under Branching it must iterate every fish and skip the eating ones. At steady state, a large fraction of fish are eating, so most iterations are wasted. The 0.96ms LookingForMeal cost under Branching vs 0.02ms under States shows the penalty of iterating irrelevant entities.

Interestingly, Branching has the lowest Submit cost (0.04ms) because there are no `MoveTo` operations or set updates -- state transitions are just field writes. States and Sets pay 0.07ms for their structural bookkeeping. In this workload the submission overhead is negligible compared to the iteration savings, but for workloads with very frequent state transitions and minimal per-entity computation, the balance could shift.

### Branching + Jobs: surprisingly competitive

Branching combined with job styles (0.71-0.73ms) is faster than Sets with non-job styles (3.44-5.52ms). Burst compilation compresses the per-entity cost so dramatically that even iterating all entities with branch checks is cheaper than iterating a subset on the main thread. This means **switching to jobs matters more than choosing the right state approach** -- though combining both (States + Jobs) gives the best result.

### LookingForMeal reveals the state approach differences

LookingForMeal is where the state approaches diverge most sharply. This system pairs idle fish with available meals -- it only operates on NotEating entities.

- **States** (0.02ms): Queries the NotEating group directly. Dense, contiguous array of only the relevant entities. The pairing logic can use 1:1 index correspondence between NotEating fish and NotEating meals.
- **Sets** (0.03ms): Filters to the NotEating set. Slightly more overhead from index indirection but still only touches relevant entities.
- **Branching** (0.96ms): Must iterate every fish, checking each one's state. At steady state, most fish are eating, so most of the iteration is wasted `continue` statements burning cache lines.

### Submit costs are consistently low

Submit times range from 0.03-0.09ms across all configurations. The submission pipeline handles structural changes (adding/removing entities, state transitions via `MoveTo`, set updates) between ticks. The low and consistent cost here means the choice of state approach doesn't impose a significant submission tax at this entity count.

## Choosing an Approach

### Iteration style

For performance-critical systems, **use jobs**. The 10-16x speedup over main-thread iteration is the single largest optimization available. Among job styles:

- **ForEachMethodAspectJob** or **ForEachMethodComponentsJob** offer the best balance of performance and ergonomics. They're within 50% of RawComponentBuffersJob while being significantly easier to write and maintain.
- **RawComponentBuffersJob** gives maximum control when you need to squeeze out every last bit of performance, at the cost of more boilerplate.

For systems that can't use jobs (e.g., they need access to managed World state), **QueryGroupSlices** is the fastest main-thread option. **ForEachMethodComponents** is a good default for simpler systems where the ~25% overhead vs QueryGroupSlices is acceptable for cleaner code.

### State approach

If entities have distinct behavioral phases where different systems operate on different subsets, **States** gives the best performance. The benefit scales with:
- **Entity count**: More entities means more cache misses saved by dense arrays
- **State skew**: If 90% of entities are in one state and a system only processes the other 10%, States avoids iterating the 90%
- **Component width**: More components accessed per entity amplifies the cache locality advantage

**Sets** are a good choice when you need subset iteration without the structural overhead of states, or when subsets overlap (an entity can be in multiple sets simultaneously, but can only be in one state). Sets also avoid the `MoveTo` copy cost, which matters for entities with many large components that transition frequently.

**Branching** is adequate for prototyping or when entity counts are low. It has zero structural overhead and the simplest code. With Burst-compiled jobs, branching is competitive even at moderate entity counts -- the CPU handles the branch prediction efficiently when the inner loop is compiled to native code.

## Running the Benchmark

1. Open the `FeedingFrenzyBenchmark` scene
2. Enter play mode
3. Use left/right arrow keys to change entity count presets (5,000 to 1,000,000 fish)
4. Use Tab/Shift+Tab to cycle iteration styles
5. Use F1/F2/F3 to switch state approaches (requires scene reload)
6. Profiling snapshots are written to `svkj_temp/profiling/fixed/`

To run the full automated sweep across all configurations, use the `frenzy-config-tester` tool:
```
frenzy-config-tester/scripts/run.sh --profile --non-deterministic-only
frenzy-config-tester/scripts/run.sh --analyze
```
