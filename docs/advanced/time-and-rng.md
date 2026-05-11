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
| `FixedElapsedTime` | Total fixed update time |
| `VariableElapsedTime` | Total variable update time |
| `Frame` | Frame counter for the current phase |
| `FixedFrame` | Fixed update frame counter |
| `VariableFrame` | Variable update frame counter |

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

Fork the RNG for sub-sequences that don't affect the parent stream:

```csharp
var forked = World.Rng.Fork();
// forked produces independent values without advancing the main RNG
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
