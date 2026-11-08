# Time & RNG

Trecs provides phase-aware time and deterministic random number generation.

## Time properties

Access time values via the `World` accessor (available in systems):

| Property | Description |
|----------|-------------|
| `DeltaTime` | Time step for the current phase (fixed or variable) |
| `FixedDeltaTime` | Fixed timestep (default 1/60s) |
| `VariableDeltaTime` | Actual frame delta time |
| `ElapsedTime` | Total elapsed time for the current phase |
| `FixedElapsedTime` | Total fixed update time: advances only by fixed steps |
| `VariableElapsedTime` | Total variable update time: never reset |
| `InterpolatedFixedElapsedTime` | Point on the fixed timeline that the current frame shows (variable phase only) |
| `Frame` | Frame counter for the current phase |
| `FixedFrame` | Fixed update frame counter |
| `VariableFrame` | Variable update frame counter: never reset |

### Three clocks

- **`FixedElapsedTime`** is simulation state. It is saved in snapshots and checksums, and grows only when a fixed step runs. Use it in fixed-update systems.
- **`VariableElapsedTime`** is the presentation clock: the sum of `VariableDeltaTime`, scaled by the runner's `TimeScale` and Unity's `Time.timeScale`. Pausing fixed update, restoring a snapshot or fast-forwarding never moves it. Use it for effects with no simulation timestamp: shader time, idle motion, UI pulses.
- **`InterpolatedFixedElapsedTime`** is where on the fixed timeline the current frame sits, the time at which `Interpolated<T>` values are blended. It lies between the previous and the newest fixed state. It holds at the newest state while fixed update is paused, and jumps at a snapshot restore, a fast-forward, or when fixed update falls behind. Use it for presentation timed from a fixed-step event, so that it stays in phase with interpolated positions: `progress = (world.InterpolatedFixedElapsedTime - startFrame * world.FixedDeltaTime) / duration`.

When fixed update falls behind real time and a frame spends more than `WorldSettings.MaxSecondsForFixedUpdatePerFrame` on it, the frame drops the steps it has not run. The simulation then runs slower than real time, and stays deterministic.

### Phase-aware DeltaTime

`DeltaTime` returns the correct value for the system's phase:

```csharp
// In a fixed update system: DeltaTime == FixedDeltaTime
public partial class PhysicsSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Position pos, in Velocity vel)
    {
        pos.Value += vel.Value * World.DeltaTime;
    }
}

// In a variable update system: DeltaTime == VariableDeltaTime
[ExecuteIn(SystemPhase.Presentation)]
public partial class AnimationSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref AnimState state)
    {
        state.Time += World.DeltaTime;
    }
}
```

Fixed-time values are also accessible from variable update via `World.FixedDeltaTime` / `World.FixedElapsedTime`.

## Deterministic RNG

Trecs provides a deterministic `Rng` seeded from `WorldSettings.RandomSeed`.

```csharp
// Phase-aware (recommended)
float value = World.Rng.Next();          // [0, 1)
float range = World.Rng.NextFloat(0f, 10f);  // [min, max)

// Phase-specific
float fixedRand = World.FixedRng.Next();
float varRand = World.VariableRng.Next();
```

Fork the RNG for independent sub-sequences:

```csharp
var forked = World.Rng.Fork();
// forked produces an independent stream; the call itself advances the parent
```

Set the seed for reproducible results:

```csharp
var settings = new WorldSettings { RandomSeed = 42 };
```

!!! warning
    For deterministic replay, always use `World.Rng` — never `UnityEngine.Random` or `System.Random`. External RNG sources are not captured in recordings.

## Time in jobs

`NativeWorldAccessor` provides time values in Burst jobs:

```csharp
float dt = nativeWorldAccessor.DeltaTime;
float elapsed = nativeWorldAccessor.ElapsedTime;
```
