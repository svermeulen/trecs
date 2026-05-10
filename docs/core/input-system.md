# Input System

The input system is a deterministic pipeline for player input. Inputs are queued by `[ExecuteIn(SystemPhase.Input)]` systems and applied at the start of the next fixed update — so inputs are captured at variable cadence but consumed at fixed cadence, which is what makes [recording and playback](../advanced/recording-and-playback.md) deterministic.

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

`World.AddInput<T>(...)` is only callable from an `[ExecuteIn(SystemPhase.Input)]` system. The Input phase runs once per fixed step, just before that step. Capture raw key/mouse state at variable cadence in the system's `Tick()`, then forward the latest value in `Execute()`:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class SnakeInputSystem : ISystem
{
    int2 _pendingDirection;

    public void Tick()  // runs every Unity Update — variable cadence
    {
        if (Input.GetKeyDown(KeyCode.W)) _pendingDirection = new int2(0, 1);
        else if (Input.GetKeyDown(KeyCode.S)) _pendingDirection = new int2(0, -1);
        else if (Input.GetKeyDown(KeyCode.A)) _pendingDirection = new int2(-1, 0);
        else if (Input.GetKeyDown(KeyCode.D)) _pendingDirection = new int2(1, 0);
    }

    public void Execute()  // runs once per fixed step
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

- Inputs are stamped with the next fixed-frame number and applied at fixed-update boundaries — the Input phase itself runs at variable cadence, but only its `AddInput` calls cross into fixed state.
- During [recording](../advanced/recording-and-playback.md), inputs are captured into the `RecordingBundle`'s `InputQueue` alongside per-frame checksums.
- During playback, `BundlePlayer.Start` disables every Input-phase system via `EnableChannel.Playback`; recorded inputs are replayed instead, and live keystrokes are ignored.
- `MissingInputBehavior` is replay-stable: the same frame produces the same component value whether or not an input was actually queued at record time.

## See also

- [Sample 11 — Snake](../samples/11-snake.md) — full keyboard-driven input wired to a recordable global entity.
- [Recording & Playback](../advanced/recording-and-playback.md) — how the `InputQueue` is captured and replayed.
