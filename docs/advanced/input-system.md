# Input System

The input system provides a deterministic pipeline for player input. Inputs are queued and applied at the start of each fixed update, ensuring consistent behavior during recording and playback.

## Defining Input Components

Mark component fields with `[Input]` in a template:

```csharp
public partial class SnakeGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
{
    [Input(MissingInputFrameBehaviour.RetainCurrent)]
    MoveInput MoveInput;
}
```

### MissingInputFrameBehaviour

Controls what happens when no input is provided for a frame:

| Behaviour | Effect |
|-----------|--------|
| `RetainCurrent` | Keep the last input value |
| `ResetToDefault` | Reset to the component's default value |

## Queuing Input

Input is queued from outside the ECS update loop (e.g., from a MonoBehaviour):

```csharp
world.AddInput(entityIndex, new MoveInput { Direction = dir });
```

## Reading Input in Systems

Input systems run first, before the fixed update phase. Mark them with `[Phase(SystemPhase.Input)]`:

```csharp
[Phase(SystemPhase.Input)]
public partial class ProcessInputSystem : ISystem
{
    void Execute([SingleEntity(typeof(GlobalTag))] in MoveInput input)
    {
        // input.Direction contains the queued input for this frame
    }
}
```

## Input and Determinism

The input system is designed for deterministic replay:

- Inputs are applied at fixed update boundaries, not at variable frame rate
- During [recording](recording-and-playback.md), inputs are captured alongside world state
- During playback, input systems are not run, and instead recorded inputs replace live input
- `MissingInputFrameBehaviour` ensures consistent behavior when frames are skipped or repeated
