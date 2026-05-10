# Input System

A Trecs simulation is designed to be **deterministic** — given the same starting state, every run produces the same world. But the values you feed *into* the simulation often aren't: keyboard / mouse / gamepad state, network packets, system clocks, asset-load timings. The input system is the controlled gateway through which those non-deterministic values enter the simulation, in a form that recording and playback can capture and replay losslessly.

Despite the name, "input" here isn't limited to user input — it covers **any non-deterministic value entering the simulation**. A network message, a wall-clock reading, a response from an external service all qualify, and they all use the same pipeline.

The mechanics:

- Mark template fields with `[Input]`.
- Inside an `[ExecuteIn(SystemPhase.Input)]` system, call `World.AddInput<T>(entity, value)`. Input systems run just before each fixed step, in lockstep with the simulation.
- The queued value is applied to the target entity at the start of the upcoming fixed step.

During [recording](../advanced/recording-and-playback.md), every `AddInput` call is captured into the recording bundle alongside the frame number it targets. During playback, every Input-phase system is disabled and the recorded inputs are replayed onto the exact same frames they originally targeted — so the simulation sees byte-identical input on every run, regardless of what the live keyboard / network / clock are doing.

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

`Retain` is right when an input represents a sustained intent (e.g. "currently holding a movement direction"). `Reset` fits one-shot signals (e.g. "fire button pressed this frame").

The choice also affects recording storage size. `Retain` lets the recorder prune successive frames with the same queued value (since `Retain` would reproduce that value anyway), so a sustained input compresses to one stored entry plus changes. With `Reset`, every frame of a sustained intent needs its own queued value or the component drops back to default — an analog-axis input recorded under `Reset` is bytes-per-frame.

## Queuing input

`World.AddInput<T>(...)` is only callable from an `[ExecuteIn(SystemPhase.Input)]` system. Like every other system, the input system's `Execute()` runs once per fixed step (zero or more times per Unity `Update`, depending on catch-up).

For **sustained** inputs (held keys, analog axes), read directly in `Execute()`:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class PlayerInputSystem : ISystem
{
    public void Execute()
    {
        var dir = new float2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        World.AddInput(World.GlobalEntityHandle, new MoveInput { Direction = dir });
    }
}
```

For **one-shot** inputs (key-down events), Unity only reports the event on the variable frame the key was pressed. If a fixed step doesn't run on that exact frame, an `Execute()` poll would miss it. The established pattern is to capture the event at variable cadence in a helper method that *you* call from your composition root's `Update`, and forward the latest value in `Execute()`:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class SnakeInputSystem : ISystem
{
    int2 _pendingDirection;

    // Called from the game's MonoBehaviour Update, every Unity frame.
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
            World.AddInput(
                World.GlobalEntityHandle,
                new MoveInput { RequestedDirection = _pendingDirection });
            _pendingDirection = int2.zero;
        }
    }
}
```

`CaptureInput()` is plain user code — there's no Trecs framework hook for variable-cadence callbacks. Wire it from wherever you already drive `world.Tick()` (typically a MonoBehaviour's `Update`).

Use `World.AddInput<T>(EntityHandle, in T)` to target a specific entity, or `entity.AddInput(value)` from inside an `[ForEachEntity]` callback that takes an `EntityAccessor`. Both forms route to the same playback-aware queue, so recordings restore inputs against the same entity on replay.

## Reading input

Input components read like any other component during fixed update:

```csharp
public partial class ProcessInputSystem : ISystem
{
    void Execute([SingleEntity(typeof(TrecsTags.Globals))] in MoveInput input)
    {
        // input.RequestedDirection is the value AddInput supplied this frame,
        // or the prior frame's value (Retain) / default (Reset)
        // if no input was queued.
    }
}
```

## See also

- [Sample 11 — Snake](../samples/11-snake.md) — full keyboard-driven input wired to a recordable global entity.
- [Recording & Playback](../advanced/recording-and-playback.md) — full record / replay workflow, the `RecordingBundle` format, and the desync detection that piggybacks on the input pipeline.
