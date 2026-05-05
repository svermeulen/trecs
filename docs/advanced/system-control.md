# Disabling and Pausing Systems

Trecs provides two independent mechanisms for skipping a system's `Execute()` on a tick. They have different determinism guarantees, are exposed on different surfaces, and are intended for different problems.

| | Channel disable | Paused |
|---|---|---|
| API | `World.SetSystemEnabled(idx, channel, bool)` | `WorldAccessor.SetSystemPaused(idx, bool)` |
| Surface | `World` (application-side) | `WorldAccessor` (simulation-side) |
| Snapshot / replay | **Not** included — ephemeral | **Included** — round-trips through serialization |
| Intended for | Editor inspector, recording playback, debug menus, kill switches | Game logic that pauses systems as part of game state (UI overlay, cutscenes) |

A system runs on a tick **iff** no channel has it disabled **and** it is not paused.

## Channels — non-deterministic toggles

Each system has an independent disable bit per `EnableChannel`. Different concerns own different channels and can never clobber each other:

```csharp
public enum EnableChannel
{
    Editor,    // Trecs Hierarchy inspector toggle
    Playback,  // Recording playback silences input systems via this channel
    User,      // Application-side user code
}
```

Game-host / editor code calls these on the `World`:

```csharp
world.SetSystemEnabled(systemIndex, EnableChannel.User, false);  // disable
world.SetSystemEnabled(systemIndex, EnableChannel.User, true);   // re-enable
bool enabled = world.IsSystemEnabled(systemIndex, EnableChannel.User);
```

Channel state defaults to "all enabled" at world init and is **not part of snapshot/restore or recording state**. Anything that sets a channel disable is responsible for re-applying it across world resets — `PlaybackHandler` does this automatically when playback (re)starts.

## SetSystemPaused — deterministic pause

`SetSystemPaused` is a per-system flag that **is** part of serialized world state, so it survives snapshot/restore and round-trips through recording playback. Use it for any pause that affects the simulation and must replay identically.

Called from inside a system or from initialization code, via `WorldAccessor`:

```csharp
accessor.SetSystemPaused(systemIndex, true);
accessor.SetSystemPaused(systemIndex, false);
bool paused = accessor.IsSystemPaused(systemIndex);
```

Like other deterministic-state mutations, this must be called from a context that can mutate fixed state — i.e., from a fixed-update system, a reactive event handler, or initialization. Calling from a variable / input system asserts.

## Combined query

For debug UIs and tests, `IsSystemEffectivelyEnabled` answers "would this system run on the next tick" — `true` iff no channel disabled and not paused:

```csharp
bool willRun = world.IsSystemEffectivelyEnabled(systemIndex);
```

## Building your own grouping

Trecs deliberately doesn't have a built-in `[SystemGroup]` concept. Instead, the framework exposes the per-system metadata you need to build whatever grouping makes sense for your game:

```csharp
int Count = world.SystemCount;
SystemMetadata meta = world.GetSystemMetadata(systemIndex);
//   meta.System         — the ISystem instance
//   meta.Phase          — Input / Fixed / Presentation / etc.
//   meta.DebugName      — human-readable name
```

A typical pattern: walk all systems once at init time, bucket their indices by some criterion the game cares about (a custom attribute, a type lookup, a config asset, anything), then drive `SetSystemPaused` or `SetSystemEnabled` calls against those buckets.

```csharp
// One-time setup: collect indices of systems tagged "gameplay".
var gameplayIndices = new List<int>();
for (int i = 0; i < world.SystemCount; i++)
{
    var meta = world.GetSystemMetadata(i);
    if (meta.System is IGameplaySystem)
    {
        gameplayIndices.Add(i);
    }
}

// Later, when the pause overlay opens (called from a fixed-update system):
foreach (var i in gameplayIndices)
{
    accessor.SetSystemPaused(i, true);
}
```

The pause overlay's UI systems aren't in the gameplay bucket, so they keep running while the gameplay systems are paused.

## When to use which

- "I want to disable some systems while a debug menu is open" → `World.SetSystemEnabled(..., EnableChannel.User, false)`.
- "I want to silence input systems while playing back a recording" → already done by `PlaybackHandler` via `EnableChannel.Playback`. Don't reimplement.
- "I want the editor to let me toggle systems for inspection" → already wired through the [Trecs Hierarchy window](../editor-windows/hierarchy.md) via `EnableChannel.Editor`.
- "I want gameplay to pause when a UI overlay is up, and have that pause survive a save / replay deterministically" → `WorldAccessor.SetSystemPaused`.
- "I want a kill switch that doesn't need to replay deterministically" → `EnableChannel.User`. It won't show up in checksums or recordings.

## Threading

Both APIs are **main-thread only**. Neither is exposed on `NativeWorldAccessor`, so jobs cannot toggle systems. Reads and writes happen at main-thread sync points (between ticks, or inside a system's `Execute()`).
