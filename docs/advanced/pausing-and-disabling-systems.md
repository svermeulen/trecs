# Pausing and Disabling Systems

Trecs provides two independent mechanisms for skipping a system's `Execute()` on a tick. They have different determinism guarantees, are exposed on different surfaces, and are intended for different problems.

| | Channel disable | Paused |
|---|---|---|
| API | `WorldAccessor.SetSystemEnabled(idx, channel, bool)` | `WorldAccessor.SetSystemPaused(idx, bool)` |
| Caller role | Any (typically a non-system `Unrestricted` accessor) | `Fixed` or `Unrestricted` only |
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

Game-host / editor code calls these on a `WorldAccessor`:

```csharp
accessor.SetSystemEnabled(systemIndex, EnableChannel.User, false);  // disable
accessor.SetSystemEnabled(systemIndex, EnableChannel.User, true);   // re-enable
bool enabled = accessor.IsSystemEnabled(systemIndex, EnableChannel.User);
```

Channel state defaults to "all enabled" at world init and is **not part of snapshot/restore or recording state**. This is likely to cause desyncs.

## SetSystemPaused — deterministic pause

`SetSystemPaused` is a per-system flag that **is** part of serialized world state, so it survives snapshot/restore and round-trips through recording playback. Use it for any pause that affects the simulation and must replay identically (eg. pausing for UI menu popup).

Called from inside a system or from initialization code, via `WorldAccessor`:

```csharp
accessor.SetSystemPaused(systemIndex, true);
accessor.SetSystemPaused(systemIndex, false);
bool paused = accessor.IsSystemPaused(systemIndex);
```

Like other deterministic-state mutations, this must be called from a context that can mutate fixed state — i.e., from a fixed-update system, a reactive event handler, or initialization. Calling from a variable / input system asserts.

## Combined query

For debug UIs and tests, `IsSystemEffectivelyEnabled` answers "would this system run on the next tick" — `true` iff no channel disabled and not paused. Available on both `World` and `WorldAccessor`:

```csharp
bool willRun = world.IsSystemEffectivelyEnabled(systemIndex);
```

## Building your own grouping

Trecs deliberately doesn't have a built-in `[SystemGroup]` concept to do a bulk pause/unpause. Instead, the framework exposes the per-system metadata you need to build whatever grouping makes sense for your game (also available on both `World` and `WorldAccessor`):

```csharp
int count = world.SystemCount;
SystemMetadata meta = world.GetSystemMetadata(systemIndex);
//   meta.System         — the ISystem instance
//   meta.Phase          — Input / Fixed / Presentation / etc.
//   meta.DebugName      — human-readable name
```

## Related

- [Accessor Roles](accessor-roles.md) — why `SetSystemPaused` requires a `Fixed`-role accessor and `SetSystemEnabled` doesn't.
- [Recording & Playback](recording-and-playback.md) — how `BundlePlayer` uses `EnableChannel.Playback` to silence input systems automatically.
- [Hierarchy Window](../editor-windows/hierarchy.md) — the editor surface for `EnableChannel.Editor` toggles.
