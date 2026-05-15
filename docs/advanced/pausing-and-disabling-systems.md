# Pausing and Disabling Systems

Trecs provides two independent mechanisms for skipping a system's `Execute()` on a tick. They differ in determinism guarantees, surface, and intent.

| | Channel disable | Paused |
|---|---|---|
| API | `WorldAccessor.SetSystemEnabled(idx, channel, bool)` | `WorldAccessor.SetSystemPaused(idx, bool)` |
| Caller role | Any (typically a non-system `Unrestricted` accessor) | `Fixed` or `Unrestricted` only |
| Snapshot / replay | **Not** included — ephemeral | **Included** — round-trips through serialization |
| Intended for | Editor inspector, recording playback, debug menus, kill switches | Game logic that pauses systems as part of game state (UI overlay, cutscenes) |

A system runs on a tick **iff** no channel has it disabled **and** it is not paused.

## Channels — non-deterministic toggles

Each system has an independent disable bit per `EnableChannel`. Different concerns own different channels and can't clobber each other:

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

Channel state defaults to "all enabled" at world init and is **not part of snapshot/restore or recording state** — using it for simulation-affecting pauses will desync.

## SetSystemPaused — deterministic pause

`SetSystemPaused` is a per-system flag that **is** part of serialized world state, so it survives snapshot/restore and round-trips through recording playback. Use it for any pause that affects the simulation and must replay identically (e.g. UI menu popup).

Called from a system or initialization code via `WorldAccessor`:

```csharp
accessor.SetSystemPaused(systemIndex, true);
accessor.SetSystemPaused(systemIndex, false);
bool paused = accessor.IsSystemPaused(systemIndex);
```

Like other deterministic-state mutations, this requires a context that can mutate fixed state — a fixed-update system, a reactive event handler, or initialization. Calling from a variable / input system asserts.

## Combined query

`IsSystemEffectivelyEnabled` answers "would this system run on the next tick" — `true` iff no channel disabled and not paused. Available on both `World` and `WorldAccessor`:

```csharp
bool willRun = world.IsSystemEffectivelyEnabled(systemIndex);
```

## Building your own grouping

Trecs has no built-in `[SystemGroup]` for bulk pause/unpause. The framework exposes the per-system registry entries so you can build whatever grouping fits your game (on `World` and `WorldAccessor`):

```csharp
IReadOnlyList<SystemEntry> systems = world.GetSystems();
SystemEntry entry = systems[systemIndex];
//   entry.System        — the ISystem instance
//   entry.Phase         — Input / Fixed / Presentation / etc.
//   entry.DebugName     — human-readable name
```

## Related

- [Accessor Roles](accessor-roles.md) — why `SetSystemPaused` requires a `Fixed`-role accessor and `SetSystemEnabled` doesn't.
- [Trecs Player Window](../editor-windows/player.md) — uses `EnableChannel.Playback` to silence input systems automatically while a recording is being replayed.
- [Hierarchy Window](../editor-windows/hierarchy.md) — the editor surface for `EnableChannel.Editor` toggles.
