# Trecs Saves Window

The **Trecs Saves** window is the library view for everything you've saved to disk: both **recordings** (time-range scrubbable buffers) and **snapshots** (single-frame world states). The [Player](player.md) owns the active recording's transport and the in-flight Save/Load convenience entries; this window owns library management — browse, search, rename, reveal in Finder, multi-select delete, save-as-new.

**To open it:** `Window > Trecs > Saves`. Worlds appear in the dropdown automatically once they call `WorldBuilder.Build()`.

!!! warning "Screenshot pending: `images/saves-overview.png`"
    Whole window with the world dropdown, search field, and both sections (Recordings, Snapshots) populated. At least one recording row should have the "loaded in Player" highlight (bold name + left accent stripe). FeedingFrenzy or Snake sample, light theme.

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![The Trecs Saves window with both sections populated](images/saves-overview.png){ width="640" }
  <figcaption>The Saves window manages all on-disk recordings and snapshots side by side.</figcaption>
</figure>
-->

## Layout

| Row | Contents |
|---|---|
| **World dropdown** | Selects which world a Load action targets and which world a Capture / Save action draws state from. |
| **Search field** | Case-insensitive substring filter across both sections. |
| **Recordings section** | Each row: name, duration, size (when wide enough), saved-at time, Load + ⋮ buttons. |
| **Snapshots section** | Each row: name, size, saved-at time, Load + ⋮ buttons. |
| **+ Save as new… row** | Trailing row in each section that prompts for a new name. |
| **Status line** | Shows the result of the last action; auto-clears after a few seconds. |

The recording **Size** column hides when the window gets narrow — Duration is the more useful measure for picking a recording to load, and the size remains in the row's tooltip.

## Recording rows

A recording row shows everything you need to pick which session to replay:

| Column | What it shows |
|---|---|
| Name | The saved-recording name. Bold when this recording is currently loaded in the [Player](player.md). |
| Duration | Total simulated time. Tooltip: frame count and tick rate. |
| Size | On-disk size. Hidden when the window is narrow. |
| Saved | Relative time (e.g. `5m ago`, `3d ago`); tooltip shows the absolute timestamp. |
| Load | Loads this recording into the Player for the selected world. Disabled when no world is active. |
| ⋮ | Per-row menu (see [Per-row menu](#per-row-menu)). |

The row currently loaded in the Player gets a **blue left accent stripe** and a bold name, so you can spot it at a glance. Selecting that row keeps the stripe; only the fill colour changes.

## Snapshot rows

Snapshot rows are a simpler shape — a snapshot is a single frame, so there's no duration:

| Column | What it shows |
|---|---|
| Name | The snapshot name. |
| Size | On-disk size. |
| Saved | Relative time. |
| Load | Stops the Player's recorder and restores world state to the snapshot's frame. If **Auto-Record** is on, a fresh recording starts at the loaded frame. |
| ⋮ | Per-row menu. |

## Search

The search field at the top filters both sections by case-insensitive substring match against the name. Empty query shows everything. Clearing the search restores the full list.

## Selection and bulk actions

!!! warning "Screenshot pending: `images/saves-selection-toolbar.png`"
    Three rows selected (mix of recordings and snapshots), with the selection toolbar visible above showing "3 selected" and the Delete / Clear buttons.

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![Multi-row selection with the bulk toolbar visible](images/saves-selection-toolbar.png){ width="540" }
  <figcaption>Shift / Ctrl click for range / additive selection; the bulk toolbar handles batch delete.</figcaption>
</figure>
-->

Click a row to select it. Selection state is independent of the Player's loaded-recording highlight.

| Gesture | Effect |
|---|---|
| Click | Replace selection with this row. |
| `Cmd/Ctrl+click` | Toggle this row in the selection. |
| `Shift+click` | Range-select within the same section using the visible (filtered) ordering. |
| Right-click | Open the row's [per-row menu](#per-row-menu) without changing the selection. |

When at least one row is selected, a **selection toolbar** appears above the list with the count and **Delete** / **Clear** buttons. Delete prompts for confirmation, then removes every selected file from disk.

## Per-row menu

Each row has a kebab (⋮) button that opens a per-row menu. Right-clicking the row opens the same menu at the cursor.

| Entry | Notes |
|---|---|
| **Save current recording into this slot** *(recordings)* | Overwrites this slot with the live in-memory recording. Disabled when there's nothing to save. |
| **Capture current frame into this snapshot** *(snapshots)* | Overwrites this slot with the current frame's world state. |
| **Rename…** | Prompts for a new name. The on-disk file is moved. |
| **Reveal in Finder** | Opens the file's location in your OS file browser. |
| **Delete** | Confirms, then removes the file from disk. |

## Save as new

The trailing **+ Save current recording as new…** row in the Recordings section prompts for a name and writes the live in-memory buffer to disk under that name. **+ Capture snapshot as new…** in the Snapshots section captures the current frame as a new snapshot. Both rows are disabled when no world is active or (for recordings) when the recorder has no buffer.

## On-disk locations

Recordings and snapshots both live under the project's `Library/` folder, namespaced by Trecs. See [`TrecsPaths`](../advanced/binary-format.md) for the exact paths and the [Binary Format](../advanced/binary-format.md) page for the on-disk layout.

## Multi-world

In a multi-world scene the dropdown selects which world a Save / Capture / Load operates on. Save/Capture pulls state from that world; Load applies the file to that world's controller. Selection state in the list is independent of the world selector.

## See also

- [Player Window](player.md) — transport, scrub, and live recording for the active world.
- [Hierarchy Window](hierarchy.md) — sibling editor window for inspecting world state.
- [Recording & Playback](../advanced/recording-and-playback.md) — the `RecordingBundle` API and `BundleRecorder` / `BundlePlayer` types.
- [Serialization](../advanced/serialization.md) — what's actually written to disk and how custom component types participate.
- [Binary Format](../advanced/binary-format.md) — header layout and on-disk paths.
