# Input System

The input system is a deterministic pipeline for player input. `[ExecuteIn(SystemPhase.Input)]` systems run just before each fixed step — in lockstep with the simulation — and queue inputs via `World.AddInput<T>(...)`. The queued inputs are applied to the global entity at the start of the fixed step that follows, so the simulation reads them deterministically: record the input stream, replay it, get the same world state. See [recording and playback](../advanced/recording-and-playback.md).

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

`World.AddInput<T>(EntityHandle, in T)` and `World.AddInput<T>(EntityIndex, in T)` are both available; the handle form is what playback restores against, so prefer it for global / persistent entities.

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

## Determinism notes

- Inputs are stamped with the next fixed-frame number when `AddInput` is called and applied at the boundary of that fixed step. Because the Input phase itself runs at fixed cadence, the input → simulation handoff is fully deterministic.
- During [recording](../advanced/recording-and-playback.md), inputs are captured into the `RecordingBundle`'s `InputQueue` alongside per-frame checksums.
- During playback, `BundlePlayer.Start` disables every Input-phase system via `EnableChannel.Playback`; recorded inputs are replayed instead, and live keystrokes are ignored.
- `MissingInputBehavior` is replay-stable: the same frame produces the same component value whether or not an input was actually queued at record time.

## See also

- [Sample 11 — Snake](../samples/11-snake.md) — full keyboard-driven input wired to a recordable global entity.
- [Recording & Playback](../advanced/recording-and-playback.md) — how the `InputQueue` is captured and replayed.
