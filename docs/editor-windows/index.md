# Editor Windows

Trecs ships three editor windows under `Window > Trecs`. They share a world dropdown so you can move between them while bound to the same `World`, and they all keep working between Play sessions via on-disk schema and recording caches.

| Window | Use it when… |
|---|---|
| [**Hierarchy**](hierarchy.md) | You want to inspect or edit live state — entities, components, tags, sets, accessors — for the world you're running. |
| [**Player**](player.md) | You want to record a session, scrub back through earlier frames, replay deterministically, fork the timeline, or pin a labelled snapshot. |
| [**Saves**](saves.md) | You want to manage the library of saved recordings and snapshots side-by-side: rename, delete, reveal on disk, load into the Player. |

All three are EditorWindow tools and can be opened, docked, and arranged like any other Unity editor panel.

## World dropdown

Each window lists every `World` whose `Initialize()` has been called this domain reload. Names come from [`WorldBuilder.SetDebugName`](../core/world-setup.md) — give your worlds readable names so the dropdown is easy to scan when you have more than one (see sample 19 — Multiple Worlds).

When no world is alive, the Hierarchy falls back to cached schema snapshots from `Library/`; the Player and Saves go inert until a world initializes.

## See also

- [World Setup](../core/world-setup.md) — for `SetDebugName` and where the windows pick worlds up from.
- [Recording &amp; Playback](../advanced/recording-and-playback.md) — the underlying `RecordingBundle` API the Player and Saves wrap.
