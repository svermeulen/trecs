# 11 — Snake

A grid-based game with deterministic input, recording/playback, and entity lifecycle management.

**Source:** `Samples/11_Snake/`

## What it does

Classic Snake — a head moves on a grid, eats food to grow, and leaves body segments behind. Hotkeys drive deterministic recording and playback:

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
    [Input(MissingInputBehavior.Retain)]
    MoveInput MoveInput;
    SnakeLength SnakeLength = new(4);
    Score Score;
    MoveTickCounter MoveTickCounter;
}
```

`[Input(Retain)]` keeps the last input until a new one arrives — needed because the snake keeps moving in its current direction.

## Systems (execution order)

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

`[SingleEntity]` is a per-parameter attribute that finds the one entity matching the given tag and binds it to the aspect parameter. Global state is read via `World.GlobalComponent<T>()`, and `World.Frame` stamps each segment with its creation frame:

```csharp
void Execute([SingleEntity(typeof(SnakeTags.SnakeHead))] in SnakeHead head)
{
    ref var counter = ref World.GlobalComponent<MoveTickCounter>().Write;

    if (counter.FramesUntilNextMove > 0)
    {
        counter.FramesUntilNextMove--;
        return;
    }

    counter.FramesUntilNextMove = _settings.FramesPerMove - 1;

    // Read input from global entity, apply turn (reject 180° reversals)
    var requested = World.GlobalComponent<MoveInput>().Read.RequestedDirection;
    // ... apply turn ...

    // Spawn segment at head's current position, tagged with creation frame
    World.AddEntity<SnakeTags.SnakeSegment>()
        .Set(new GridPos(head.GridPos))
        .Set(new SegmentAge(World.Frame));

    // Advance head and wrap around grid edges
    int size = _settings.GridSize;
    var newPos = head.GridPos + head.Direction;
    newPos.x = ((newPos.x % size) + size) % size;
    newPos.y = ((newPos.y % size) + size) % size;
    head.GridPos = newPos;
}

partial struct SnakeHead : IAspect, IWrite<Direction, GridPos> { }
```

### 3. FoodConsumeSystem

If the head overlaps food, removes the food entity and increments `SnakeLength` and `Score`.

### 4. SegmentTrimSystem

When segment count exceeds `SnakeLength - 1`, removes the oldest segment (by `SegmentAge`).

### 5. FoodSpawnSystem

Spawns food up to a maximum count at random unoccupied grid cells using `World.Rng`.

### 6. SnakeRendererSystem (`[ExecuteIn(SystemPhase.Presentation)]`)

Maps `GridPos` to world coordinates for rendering.

## Determinism & recording

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

Serialization is wired up via the sample-side `SerializationFactory.CreateAll(world)` helper (in `Samples/Common/Scripts/`), which composes a registry + `WorldStateSerializer` + `SnapshotSerializer` + `BundleRecorder` + `BundlePlayer` + `RecordingBundleSerializer`. The `RecordAndPlaybackController` reads keyboard input and drives `SaveSnapshot(path)` / `LoadSnapshot(path)` for snapshots, and `recorder.Start()` / `recorder.Stop()` + `bundleSerializer.Save(bundle, path)` / `bundleSerializer.Load(path)` + `player.Start(bundle)` for recording bundles, against file paths under `persistentDataPath`.

See [Serialization](../advanced/serialization.md) for custom-serializer authoring and [Recording & Playback](../advanced/recording-and-playback.md) for the full bundle API.

## Concepts introduced

- **`[Input(Retain)]`** on a template field — input persists until replaced
- **`[ExecuteIn(SystemPhase.Input)]`** — runs in the input phase, before fixed update
- **`World.AddInput`** — queues input from outside the ECS tick
- **`[SingleEntity(typeof(Tag))]`** parameter — binds the one tagged entity into the `Execute` signature
- **Grid-based gameplay** — integer positions, discrete movement
- **FIFO entity management** — `SegmentAge` tracks creation order for oldest-first removal
- **Deterministic recording/playback** — seeded RNG + deterministic submission
