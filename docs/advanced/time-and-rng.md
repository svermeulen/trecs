# Time & RNG

Trecs provides phase-aware time and deterministic random number generation.

## Time Properties

Access time values via the `World` accessor (available in systems as `World`):

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

### Phase-Aware DeltaTime

`DeltaTime` automatically returns the correct value for the phase your system runs in:

```csharp
// In a fixed update system: DeltaTime == FixedDeltaTime
public partial class PhysicsSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Position pos, in Velocity vel)
    {
        pos.Value += vel.Value * World.DeltaTime;  // Uses FixedDeltaTime
    }
}

// In a variable update system: DeltaTime == VariableDeltaTime
[ExecuteIn(SystemPhase.Presentation)]
public partial class AnimationSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref AnimState state)
    {
        state.Time += World.DeltaTime;  // Uses VariableDeltaTime
    }
}
```

You can also access the fixed time values while in variable update via World.FixedDeltaTime / World.FixedElapsedTime

## Deterministic RNG

Trecs provides a deterministic `Rng` type seeded from `WorldSettings.RandomSeed`.

### Using RNG

```csharp
// Phase-aware (recommended)
float value = World.Rng.Next();          // [0, 1)
float range = World.Rng.NextFloat(0f, 10f);  // [min, max)

// Phase-specific
float fixedRand = World.FixedRng.Next();
float varRand = World.VariableRng.Next();
```

### Forking RNG

Fork the RNG for sub-sequences that don't affect the parent sequence:

```csharp
var forked = World.Rng.Fork();
// forked produces independent values without advancing the main RNG
```

### Seeding

Set the seed in world settings for reproducible results:

```csharp
var settings = new WorldSettings
{
    RandomSeed = 42
};
```

!!! warning
    For deterministic replay, always use `World.Rng` — never `UnityEngine.Random` or `System.Random`. External RNG sources are not captured in recordings.

## Time in Jobs

`NativeWorldAccessor` provides time values in Burst jobs:

```csharp
float dt = nativeWorldAccessor.DeltaTime;
float elapsed = nativeWorldAccessor.ElapsedTime;
```
