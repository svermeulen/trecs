# 11 — Snake

A complete grid-based game with deterministic input handling, recording/playback, and structured entity lifecycle management.

**Source:** `Samples/11_Snake/`

## What It Does

Classic Snake — a head moves on a grid, eats food to grow, and leaves a trail of body segments. The game supports deterministic recording and playback via F5/F6/F7 keys.

## Schema

### Components

```csharp
public struct GridPos : IEntityComponent { public int2 Value; }
public struct Direction : IEntityComponent { public int2 Value; }
public struct MoveInput : IEntityComponent { public int2 RequestedDirection; }
public struct SegmentAge : IEntityComponent { public int Value; }
public struct SnakeLength : IEntityComponent { public int Value; }
public struct Score : IEntityComponent { public int Value; }
public struct MoveTickCounter : IEntityComponent { public int FramesUntilNextMove; }
```

### Tags

```csharp
public struct SnakeHead : ITag { }
public struct SnakeSegment : ITag { }
public struct SnakeFood : ITag { }
```

### Templates

```csharp
public partial class SnakeGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
{
    [Input(MissingInputFrameBehaviour.RetainCurrent)]
    public MoveInput MoveInput;
    public SnakeLength SnakeLength = new(4);
    public Score Score;
    public MoveTickCounter MoveTickCounter;
}
```

The `[Input(RetainCurrent)]` attribute ensures the last input persists until a new one is received — critical for a game where the snake keeps moving in its current direction.

## Systems (Execution Order)

### 1. SnakeInputSystem (`[InputSystem]`)

Captures WASD input each visual frame and queues it:

```csharp
[InputSystem]
public partial class SnakeInputSystem : ISystem
{
    int2 _pendingDirection;

    // Called each visual frame to capture input
    public void Tick()
    {
        if (Input.GetKeyDown(KeyCode.W)) _pendingDirection = new int2(0, 1);
        if (Input.GetKeyDown(KeyCode.S)) _pendingDirection = new int2(0, -1);
        // ...
    }

    public void Execute()
    {
        if (_pendingDirection.x != 0 || _pendingDirection.y != 0)
        {
            World.AddInput(World.GlobalEntityHandle, new MoveInput { RequestedDirection = _pendingDirection });
            _pendingDirection = int2.zero;
        }
    }
}
```

### 2. SnakeMovementSystem

Every N fixed frames (controlled by `MoveTickCounter`):

1. Reads pending turn input (rejects 180° reversals)
2. Spawns a new segment at the head's current position
3. Advances the head one cell in the current direction
4. Wraps around grid edges

### 3. FoodConsumeSystem

Checks if the head overlaps food. If so: removes the food entity, increments `SnakeLength` and `Score`.

### 4. SegmentTrimSystem

If the segment count exceeds `SnakeLength - 1`, removes the oldest segment (by `SegmentAge`).

### 5. FoodSpawnSystem

Spawns food up to a maximum count at random unoccupied grid cells using `World.Rng`.

### 6. SnakeRendererSystem (`[VariableUpdate]`)

Maps `GridPos` to world coordinates for rendering.

## Determinism & Recording

The world is configured for deterministic replay:

```csharp
new WorldBuilder()
    .SetSettings(new WorldSettings
    {
        RequireDeterministicSubmission = true,
        RandomSeed = settings.RandomSeed,
    })
    // ...
```

The `RecordAndPlaybackController` handles F5 (record), F6 (stop), F7 (playback), F8/F9 (bookmarks).

## Concepts Introduced

- **`[Input(RetainCurrent)]`** — input persists across frames until replaced
- **`[InputSystem]`** — system runs in the input phase, before fixed update
- **`AddInput()`** — queues input from outside the ECS tick
- **Grid-based gameplay** — integer positions, discrete movement
- **FIFO entity management** — `SegmentAge` tracks creation order for oldest-first removal
- **Deterministic recording/playback** — seeded RNG + deterministic submission
