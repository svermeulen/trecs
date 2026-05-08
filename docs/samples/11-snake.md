# 11 — Snake

A complete grid-based game with deterministic input handling, recording/playback, and structured entity lifecycle management.

**Source:** `Samples/11_Snake/`

## What It Does

Classic Snake — a head moves on a grid, eats food to grow, and leaves a trail of body segments. The game supports deterministic recording and playback via hotkeys:

| Key | Action |
|---|---|
| F5 | Toggle recording |
| F6 | Toggle playback |
| F8 | Save snapshot |
| F9 | Load snapshot |

Recordings and snapshots are written under `{Application.persistentDataPath}/Snake/Recordings/`.

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
    [Input(MissingInputBehavior.RetainCurrent)]
    MoveInput MoveInput;
    SnakeLength SnakeLength = new(4);
    Score Score;
    MoveTickCounter MoveTickCounter;
}
```

The `[Input(RetainCurrent)]` attribute ensures the last input persists until a new one is received — critical for a game where the snake keeps moving in its current direction.

## Systems (Execution Order)

### 1. SnakeInputSystem (`[ExecuteIn(SystemPhase.Input)]`)

Captures WASD input each visual frame and queues it:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class SnakeInputSystem : ISystem
{
    int2 _pendingDirection;

    // Called each visual frame to capture input
    public void Tick()
    {
        if (Input.GetKeyDown(KeyCode.W)) _pendingDirection = new int2(0, 1);
        else if (Input.GetKeyDown(KeyCode.S)) _pendingDirection = new int2(0, -1);
        else if (Input.GetKeyDown(KeyCode.A)) _pendingDirection = new int2(-1, 0);
        else if (Input.GetKeyDown(KeyCode.D)) _pendingDirection = new int2(1, 0);
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

Demonstrates `World.GlobalComponent<T>()` for reading/writing global state, and `World.Frame` for tracking creation order:

```csharp
public void Execute()
{
    ref var counter = ref World.GlobalComponent<MoveTickCounter>().Write;

    if (counter.FramesUntilNextMove > 0)
    {
        counter.FramesUntilNextMove--;
        return;
    }

    counter.FramesUntilNextMove = _settings.FramesPerMove - 1;

    var head = SnakeHead.Query(World).WithTags<SnakeTags.SnakeHead>().Single();

    // Read input from global entity
    var requested = World.GlobalComponent<MoveInput>().Read.RequestedDirection;
    // ... apply turn, reject 180° reversals ...

    // Spawn segment at head's current position, tagged with creation frame
    World.AddEntity<SnakeTags.SnakeSegment>()
        .Set(new GridPos(head.GridPos))
        .Set(new SegmentAge(World.Frame));

    // Advance head and wrap around grid edges
    var newPos = head.GridPos + head.Direction;
    newPos.x = ((newPos.x % size) + size) % size;
    newPos.y = ((newPos.y % size) + size) % size;
    head.GridPos = newPos;
}

partial struct SnakeHead : IAspect, IWrite<Direction, GridPos> { }
```

### 3. FoodConsumeSystem

Checks if the head overlaps food. If so: removes the food entity, increments `SnakeLength` and `Score`.

### 4. SegmentTrimSystem

If the segment count exceeds `SnakeLength - 1`, removes the oldest segment (by `SegmentAge`).

### 5. FoodSpawnSystem

Spawns food up to a maximum count at random unoccupied grid cells using `World.Rng`.

### 6. SnakeRendererSystem (`[ExecuteIn(SystemPhase.Presentation)]`)

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

Serialization is wired in via the sample-side `SerializationFactory.CreateAll(world)` helper (in `Samples/Common/Scripts/`), which composes a registry + `WorldStateSerializer` + `SnapshotSerializer` + `BundleRecorder` + `BundlePlayer` + `RecordingBundleSerializer`. The `RecordAndPlaybackController` reads keyboard input and drives `SaveSnapshot(path)` / `LoadSnapshot(path)` to manage snapshot files, and `recorder.Start()` / `recorder.Stop()` + `bundleSerializer.Save(bundle, path)` / `bundleSerializer.Load(path)` + `player.Start(bundle)` to manage recording bundles, against file paths under `persistentDataPath`.

See [Serialization](../advanced/serialization.md) for custom-serializer authoring and [Recording & Playback](../advanced/recording-and-playback.md) for the full bundle API.

## Concepts Introduced

- **`[Input(RetainCurrent)]`** — input persists across frames until replaced
- **`[ExecuteIn(SystemPhase.Input)]`** — system runs in the input phase, before fixed update
- **`AddInput()`** — queues input from outside the ECS tick
- **Grid-based gameplay** — integer positions, discrete movement
- **FIFO entity management** — `SegmentAge` tracks creation order for oldest-first removal
- **Deterministic recording/playback** — seeded RNG + deterministic submission
