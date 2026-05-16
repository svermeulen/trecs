# Input System

A Trecs simulation is **deterministic**: given the same starting state, every run produces the same world. But the values you feed *into* the simulation often aren't — keyboard / mouse / gamepad state, network packets, system clocks, asset-load timings. The input system is the controlled gateway for those non-deterministic values, in a form recording and playback can capture and replay losslessly.

Despite the name, "input" isn't limited to user input — it covers **any non-deterministic value entering the simulation**. Network messages, wall-clock readings, and responses from external services all use the same pipeline.

The mechanics:

- Mark template fields with `[Input]`.
- Inside an `[ExecuteIn(SystemPhase.Input)]` system, call `entity.AddInput<T>(World, value)`. Input systems run just before each fixed step, in lockstep with the simulation — see the [phase diagram](../core/systems.md#phase-diagram).
- The queued value is applied to the target entity at the start of the upcoming fixed step.

When the [Trecs Player window](../editor-windows/player.md) records, every `AddInput` call is captured alongside its target frame. During scrub or replay, Input-phase systems are disabled and recorded inputs are replayed onto the exact frames they originally targeted, so the simulation sees byte-identical input on every playback.

## Marking input fields

Mark template fields with `[Input]`:

```csharp
public partial class SnakeGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
{
    [Input(MissingInputBehavior.Retain)]
    MoveInput MoveInput;
}
```

`MissingInputBehavior` controls what happens when no input is queued for a frame:

| Value | Effect |
|-------|--------|
| `Retain` | Keep the previous frame's value |
| `Reset` | Reset to the component's default value |

`Retain` fits sustained intent (e.g. "currently holding a movement direction"). `Reset` fits one-shot signals (e.g. "fire button pressed this frame").

The choice also affects recording size. `Retain` lets the recorder prune successive frames with the same queued value (since `Retain` would reproduce it anyway); `Reset` does the same when the default value is enqueued.

## Queuing input

`AddInput<T>(world, value)` is only callable from an `[ExecuteIn(SystemPhase.Input)]` system. Its `Execute()` runs once per fixed step (zero or more times per Unity `Update`, depending on catch-up — see [Per-frame execution order](systems.md#phase-diagram)).

For **sustained** inputs (held keys, analog axes), read directly in `Execute()`:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class PlayerInputSystem : ISystem
{
    public void Execute()
    {
        var dir = new float2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        World.GlobalEntityHandle.AddInput(World, new MoveInput { Direction = dir });
    }
}
```

For **one-shot** inputs (key-down events), Unity only reports the event on the variable frame the key was pressed. If a fixed step doesn't run on that frame, an `Execute()` poll would miss it. Capture the event at variable cadence and forward the latest value in `Execute()`:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class SnakeInputSystem : ISystem
{
    int2 _pendingDirection;

    // Called every rendered frame
    // for eg. from the game's MonoBehaviour Update, or an early presentation system
    public void CaptureInput()
    {
        if (Input.GetKeyDown(KeyCode.W)) _pendingDirection = new int2(0, 1);
        else if (Input.GetKeyDown(KeyCode.S)) _pendingDirection = new int2(0, -1);
        else if (Input.GetKeyDown(KeyCode.A)) _pendingDirection = new int2(-1, 0);
        else if (Input.GetKeyDown(KeyCode.D)) _pendingDirection = new int2(1, 0);
    }

    public void Execute()  // runs just before each fixed step
    {
        if (_pendingDirection.x != 0 || _pendingDirection.y != 0)
        {
            World.GlobalEntityHandle.AddInput(
                World,
                new MoveInput { RequestedDirection = _pendingDirection });
            _pendingDirection = int2.zero;
        }
    }
}
```

Call `handle.AddInput<T>(World, value)` (or the matching method on `EntityIndex` / aspect) to target a specific entity. The same method exists inside an `[ForEachEntity]` callback that takes an `EntityHandle` parameter.

## Reading input

Input components read like any other component during fixed update:

```csharp
public partial class ProcessInputSystem : ISystem
{
    void Execute([SingleEntity(typeof(TrecsTags.Globals))] in MoveInput input)
    {
        // input.RequestedDirection is the value AddInput supplied this frame,
        // or the prior frame's value (Retain) / default (Reset) if none was queued.
    }
}
```

`[Input]` components are read-only everywhere — including inside Input systems themselves. The only way a value reaches an `[Input]` field is via `entity.AddInput<T>(world, value)`, which is gated to `[ExecuteIn(SystemPhase.Input)]` systems. A direct `.Write` on the component throws in DEBUG builds regardless of which phase the caller is in.

## See also

- [Sample 11 — Snake](../samples/11-snake.md) — full keyboard-driven input wired to a recordable global entity.
- [Trecs Player Window](../editor-windows/player.md) — records every `AddInput` call into a scrubbable buffer and replays them on playback.
