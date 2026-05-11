# Editor Windows

Trecs ships three editor windows under `Window > Trecs`. They share a world dropdown so you can move between them bound to the same `World`, and persist across Play sessions via on-disk schema and recording caches.

| Window | Use it when… |
|---|---|
| [**Hierarchy**](hierarchy.md) | Inspect or edit live state — entities, components, tags, sets, accessors. |
| [**Player**](player.md) | Record a session, scrub back, replay, fork the timeline, or pin a labelled snapshot. |
| [**Saves**](saves.md) | Manage saved recordings and snapshots: rename, delete, reveal on disk, load into the Player. |

All three are EditorWindow tools — open, dock, and arrange them like any Unity editor panel.

## World dropdown

Each window lists every `World` whose `Initialize()` has been called this domain reload. Names come from [`WorldBuilder.SetDebugName`](../core/world-setup.md) — set readable names so the dropdown is easy to scan with more than one world (see sample 19 — Multiple Worlds).

When no world is alive, the Hierarchy falls back to cached schema snapshots from `Library/`; the Player and Saves go inert until a world initializes.

## See also

- [World Setup](../core/world-setup.md) — for `SetDebugName` and where the windows pick worlds up from.
- [Recording &amp; Playback](../advanced/recording-and-playback.md) — the underlying `RecordingBundle` API the Player and Saves wrap.
