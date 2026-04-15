# State Benchmark: Dense Arrays vs Filters vs Branching

This sample compares three approaches to handling entity state transitions in a group-based ECS, using a "fish must eat" simulation where entities cycle between NotEating and Eating states.

## The Simulation

- 60,000 fish entities and 48,000 meal entities
- Fish spawn as NotEating, get paired with a meal, move toward it (Eating), consume it, then return to NotEating
- At steady state, a large fraction of fish are in the Eating state at any given time
- Four systems run each tick: SpawnFood, LookingForFood, ConsumingFood, Movement
- Only Eating fish need velocity computation and movement; only NotEating fish need food pairing

## The Three Approaches

### WithStates (Trecs group-based states)

Each template declares explicit states via `IHasState<NotEating>` and `IHasState<Eating>`. Each state is a separate dense array with identical component layout. Systems query specific state groups:

```
MovementSystem queries (Fish, Eating) -> iterates a dense, contiguous array
LookingForFood queries (Fish, NotEating) -> iterates a different dense array
```

State transitions use `MoveTo`, which copies the entity's component data from one group's arrays to another during `SubmitEntities`.

**Memory access pattern**: Sequential. `positions[0], positions[1], positions[2], ...`

### WithoutStates (bool component + branching)

Single group per template. Each fish has a `bool IsEating` field. All systems iterate every fish and branch:

```
MovementSystem iterates ALL fish:
  for each fish:
    if (!IsEating) continue;  // skip
    apply velocity
```

State transitions are a simple field write: `IsEating = true/false`.

**Memory access pattern**: Sequential but wasteful. Reads every entity's data even if skipping it. Branch predictor handles the skipping, but cache lines are loaded for entities that won't be processed.

### WithFilters (persistent filter + index indirection)

Single group per template. Two persistent filters track which fish are Eating vs NotEating. Systems iterate the filter's index list, which provides scattered pointers into the main component arrays:

```
MovementSystem iterates the EatingFish filter:
  for each filter index:
    idx = filterIndices[i]     // could be any value: 42, 7891, 103, ...
    positions[idx] += velocity  // scattered access
```

State transitions add/remove from filters (hash-based operations).

**Memory access pattern**: Scattered. `positions[42], positions[7891], positions[103], ...` -- poor cache locality because filter indices lose their natural order through swap-back removal (removing an entry swaps the last element into the vacated slot, scrambling the iteration order over time). The underlying indices are a subset of `[0, N)` and *could* be iterated in order, but the filter's internal storage strategy doesn't preserve this. Sorting the indices before iteration recovers ~10% of the lost performance, but the sort itself (O(N log N) per frame) costs more than it saves.

## Results (60k fish, relative to WithFilters baseline)

```
WithStates:      -22%  (fastest)
WithFilters:     baseline
WithoutStates:   +7%   (slowest)
```

### Per-System Breakdown

| System | WithStates | WithFilters | WithoutStates |
|--------|-----------|-------------|---------------|
| ConsumingFood | 5.90ms | 7.01ms | 6.29ms |
| Movement | 5.19ms | 7.05ms | 6.70ms |
| LookingForFood | 0.08ms | 1.35ms | 1.98ms |
| SubmitEntities | 0.30ms | 0.15ms | 0.04ms |
| **Total FixedTick** | **11.53ms** | **~15ms** | **15.03ms** |

## Analysis

### Why WithStates wins

States give you both advantages simultaneously: you iterate **only** the entities you care about (no wasted work), and you iterate them **sequentially** in contiguous memory (optimal cache behavior). The 22% win comes from:

- **Movement (-12% vs filters)**: The clearest demonstration. Both approaches iterate the same number of entities (only eating fish), but WithStates accesses `positions[0], [1], [2]...` while WithFilters accesses `positions[42], [7891], [103]...`. The sequential pattern keeps the CPU prefetcher happy and minimizes cache misses.

- **LookingForFood (-9.6% of total vs filters)**: WithStates queries the NotEating group directly -- a small dense array. WithFilters iterates a filter with scattered access to fish data, plus a sequential meal scan. WithoutStates iterates all 60k fish checking bools.

- **SubmitEntities (+2.2%)**: The only cost of states. `MoveTo` copies entity data between groups during submission. This is the price for maintaining the dense array invariant.

### Why WithoutStates and WithFilters are close (+7% difference)

This is the most surprising result. Two very different strategies -- sequential-with-branching vs scattered-without-branching -- end up within 7% of each other:

- **WithoutStates** loads cache lines for every entity (wasteful) but accesses them sequentially (cache-friendly). The branch predictor handles the `if (!IsEating) continue` cheaply once the pattern stabilizes.

- **WithFilters** only touches entities it needs (efficient) but accesses them at scattered indices (cache-unfriendly). Each component access at a random index is a potential cache miss.

These two penalties roughly cancel out at this entity count and eating/not-eating ratio.

### Where each approach has overhead

- **WithStates**: SubmitEntities pays for move operations. More states = more groups = more overhead for cross-cutting queries ("all fish regardless of state").
- **WithoutStates**: Every system touching fish iterates all of them, even if most are irrelevant. Cost scales with total entity count, not active entity count.
- **WithFilters**: Scattered access pattern. Filter add/remove are hash operations. Filter maintenance during entity removal has overhead (visible in SubmitEntities/Remove Filters).

## Lessons for ECS Design

### 1. States are worth it when entities have distinct behavioral phases

If different systems operate on different subsets of the same entity type, states give a measurable win. The benefit scales with:
- **Entity count**: More entities = more cache misses saved
- **State skew**: If 90% of entities are in one state and a system only processes the other 10%, states avoid iterating the 90%
- **Component access width**: More component arrays touched per entity = more cache misses per scattered access

### 2. The bool/branch approach is surprisingly competitive

For simple cases where you're prototyping or the entity count is moderate, a bool component with an `if` check in each system is adequate. The CPU branch predictor is effective, and sequential memory access partially compensates for the wasted iteration. This is the pragmatic choice when you don't want to commit to a full state machine upfront.

### 3. Filters and states solve different problems

Persistent filters and states both let you iterate subsets of entities, but they optimize for different things:

- **States** optimize for **iteration throughput**. Entities are physically separated into dense arrays, giving sequential cache access. The cost is structural: `MoveTo` copies component data between groups, and the combinatorial state space must be declared upfront.

- **Filters** optimize for **flexibility**. They tag subsets of entities within a group without moving data. Multiple filters can overlap, they don't require upfront declaration, and add/remove are simple hash operations. The cost is that iteration uses index indirection into the shared component arrays, losing cache locality.

If your use case involves exclusive, well-defined behavioral phases where systems operate on specific subsets (eating/not-eating, alive/dead), use states -- the dense array benefit pays for the structural overhead. Use filters for dynamic, overlapping, or ad-hoc subsets that don't map cleanly to exclusive states (e.g., "entities visible to camera", "entities near the player", "entities damaged this frame").

The scattered access in the current filter implementation is partly an artifact of swap-back removal scrambling index order. A bitset-based filter or one that maintained sorted indices could close part of the gap, but even then, filters fundamentally share a single component array across all subsets, while states give each subset its own contiguous memory. For high-throughput iteration, that physical separation is what matters.

### 4. SubmitEntities cost is the tax on structural correctness

WithStates pays ~0.3ms per tick for move operations. WithoutStates pays ~0.04ms (no moves). This is the cost of maintaining physically separated, dense arrays. For this workload, the 0.26ms tax saves ~3.5ms in system iteration -- a clear win. But for workloads with very frequent state transitions and minimal per-entity computation, the move overhead could dominate.

### 5. Profile the actual workload

The relative performance of these approaches depends on the specific simulation: entity count, state distribution, number of component arrays accessed per system, transition frequency, and system complexity. This benchmark provides a baseline, but your mileage will vary. The tools are here to test your specific case.

## Running the Benchmark

1. Open the `StateBenchmark` scene
2. Select the Bootstrap GameObject
3. Set the `Approach` field to WithStates, WithoutStates, or WithFilters
4. Enter play mode
5. Profiling snapshots are written to `svkj_temp/profiling/fixed/`
6. Use the profiling inspector or read the JSON files directly to compare
