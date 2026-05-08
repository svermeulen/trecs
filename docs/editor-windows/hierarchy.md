# Hierarchy Window

The **Trecs Hierarchy** window is the editor's main inspector for a running world's schema and live state. It cross-references templates, entities, components, tags, sets, and accessors in a single tree, and stays useful between Play sessions via on-disk schema snapshots.

**To open it:** `Window > Trecs > Hierarchy`. Worlds appear in the dropdown automatically once they call `WorldBuilder.Build()` — give them readable names with [`WorldBuilder.SetDebugName`](../core/world-setup.md) so they're easy to pick when you have more than one.

!!! warning "Screenshot pending: `images/hierarchy-overview.png`"
    Whole window with all five sections expanded; entity rows visible under a couple of templates. FeedingFrenzy sample, light theme.

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![The Trecs Hierarchy window with all five sections expanded](images/hierarchy-overview.png){ width="720" }
  <figcaption>The Trecs Hierarchy window in live mode against the FeedingFrenzy sample.</figcaption>
</figure>
-->

## Overview

The window shows a tree split into five top-level sections:

| Section | What it lists |
|---|---|
| **Templates** | Every concrete and abstract template, grouped by tag-set partition; entity rows hang under their template. |
| **Accessors** | Every system / manual accessor, grouped by execution phase. |
| **Components** | Every component type registered with the world. |
| **Sets** | Every `EntitySet` with its current tag membership. |
| **Tags** | Every `Tag` and which entities/templates carry it. |

A toolbar across the top hosts:

- A **world dropdown** (multi-world projects — see below).
- A **search field** with the predicate DSL described in [Search syntax](#search-syntax).
- A `?` button that toggles an inline help panel (mirrors this page).
- The Unity-standard kebab (`⋮`) **cog menu** for window options.

## World dropdown

!!! warning "Screenshot pending: `images/hierarchy-world-dropdown.png`"
    Toolbar with the world dropdown clicked open, ideally showing both a live world and at least one `(cached)` entry so the suffix is visible.

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![Toolbar with the world dropdown open showing live and cached entries](images/hierarchy-world-dropdown.png){ width="480" }
  <figcaption>Live worlds appear by name; cached snapshots are suffixed with <code>(cached)</code>.</figcaption>
</figure>
-->

When at least one Trecs world is alive in the editor, the dropdown lists each world by name. Picking one binds the tree to that world.

When no live world exists, the dropdown falls back to **cached snapshots** loaded from `Library/`. Cached entries are suffixed with `(cached)` so you can tell them apart from live worlds. Cache mode is read-only — toggles like the system-enable switch are disabled, since there is no running world to mutate.

Selecting a live world while a cached snapshot is showing automatically switches the window into live mode (and vice-versa when the live world stops).

## Search syntax

!!! warning "Screenshot pending: `images/hierarchy-search-help.png`"
    Whole window with the `?` button toggled on so the inline help panel is visible above the tree. Bonus: type `t:e tag:enemy` in the search field so a filtered tree shows underneath.

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![Hierarchy window with the inline search-help panel toggled open](images/hierarchy-search-help.png){ width="720" }
  <figcaption>The <code>?</code> button toggles an inline reference panel that mirrors this section.</figcaption>
</figure>
-->

The search field accepts whitespace-separated **tokens**. Every token must match (logical AND) for a row to appear. Tokens fall into three shapes:

### Bare substrings

A bare word is a substring match against the row's display name and altname:

```
player        matches any row whose display name contains "player"
```

Smart-case: a token with no uppercase characters matches case-insensitively; introducing any uppercase character flips that token to case-sensitive (ripgrep / vim / ag convention).

```
play COLL     "play" matches loosely, "COLL" only matches literal "COLL"
```

Quote a phrase to include spaces:

```
"My Long Name"
```

### Kind selector

A single `t:KIND` token restricts which row kinds are visible. Only one selector is honored; later selectors overwrite earlier ones.

| Token | Restricts to |
|---|---|
| `t:e`, `t:entity`, `t:entities` | Entity rows |
| `t:t`, `t:template`, `t:templates` | Template rows |
| `t:c`, `t:component`, `t:components` | Component rows |
| `t:s`, `t:set`, `t:sets` | Set rows |
| `t:tag`, `t:tags` | Tag rows |
| `t:a`, `t:accessor`, `t:accessors` | Accessor rows |

### Predicates

Predicates are `key:value` tokens with a recognized key. Each predicate is checked against whatever fields the row's kind exposes. A predicate that doesn't apply to a row's kind filters that row out, so combining predicates implicitly narrows which kinds appear.

| Predicate | Applies to | Matches when |
|---|---|---|
| `tag:X` | Templates, entities, sets, tags | The row's tag list contains a tag whose name contains `X` |
| `c:X` (alias `component:X`) | Templates, entities, components | The row has a component whose type name contains `X` |
| `base:X` | Templates | The template's base chain contains a template whose name contains `X` |
| `derived:X` | Templates | A derived template's name contains `X` (i.e. `X` extends this row) |
| `template:X` | Entities | The entity's template name contains `X` |
| `reads:X` | Accessors | The accessor reads a component whose name contains `X` |
| `writes:X` | Accessors | The accessor writes a component whose name contains `X` |
| `accesses:X` | Accessors | The accessor reads OR writes a component whose name contains `X` |

Unrecognized `key:value` tokens are treated as bare substrings.

### Modifiers

| Modifier | Effect |
|---|---|
| `-tok` | Negate. Token must NOT match. Works on bare substrings and predicates. |
| `"a b c"` | Quoted phrase — a single substring that may contain spaces. |

### Examples

```
player                       any row matching "player"
tag:player                   any kind tagged "player"
t:e tag:player               entities tagged "player"
t:e tag:enemy -c:Boss        enemies that don't have a Boss component
t:t c:Health                 templates with a Health component
t:e tag:enemy c:Health       entities tagged enemy AND with Health
t:a reads:Health             accessors that read Health
t:a accesses:Position        accessors that read or write Position
base:Enemy                   templates whose base chain includes Enemy
"My Long Name"               substring including spaces
```

## Search history

Every committed search is recorded (capped at 20 entries, persisted across sessions via `EditorPrefs`):

- With focus in the search field, **Up** / **Down** cycle through prior queries.
- The current draft is preserved on the first **Up** and restored when you walk back past it with **Down**.
- The cog menu has a **Clear Search History** entry.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Cmd/Ctrl+F` | Focus the search field |
| `Esc` | Clear the search field |
| `Up` / `Down` (in search field) | Recall recent queries |
| `Alt+Left` / `Alt+Right` | Walk back / forward through prior selections |
| `Shift+hover` (inspector link) | Preview-scroll to the linked row without selecting it |

## Row context menu

!!! warning "Screenshot pending: `images/hierarchy-row-context-menu.png`"
    Right-click a Component row (e.g. `CHealth`) so the full Component menu is visible — its six entries cover the most variety. Tree expanded behind it for context.

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![Row context menu opened on a component, showing the scoped Find variants](images/hierarchy-row-context-menu.png){ width="520" }
  <figcaption>Each "Find…" entry pre-fills the search field with the right predicate and kind selector.</figcaption>
</figure>
-->

Right-clicking a row offers actions tailored to the row's kind:

| Row kind | Actions |
|---|---|
| Any | Copy Name |
| Template | Find Entities Of This Template · Find Templates Derived From This · Find Templates This Derives From |
| Component | Find Anything With This Component · Find Templates With This Component · Find Entities With This Component · Find Accessors That Read or Write This · Find Accessors That Read This · Find Accessors That Write This |
| Tag | Find Anything With This Tag · Find Templates With This Tag · Find Entities With This Tag · Find Sets With This Tag |
| Set | Find Sets With Same Name |
| Entity | Copy Entity Id |
| Accessor | (no extra entries — accessor cross-links live in the inspector) |

Each "Find…" entry pre-fills the search field with the right predicate and kind selector.

## Cog menu

!!! warning "Screenshot pending: `images/hierarchy-cog-menu.png`"
    Window with the kebab (`⋮`) menu open in the top-right, showing all four entries (`Show Empty Templates`, `Show Abstract Templates`, `Help…`, `Clear Search History`).

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![The cog menu open showing the visibility toggles, help, and clear-search-history entries](images/hierarchy-cog-menu.png){ width="480" }
  <figcaption>The kebab menu hosts visibility toggles and search-history maintenance.</figcaption>
</figure>
-->

The kebab (`⋮`) at the top-right of the window holds:

- **Show Empty Templates** — toggle whether templates with no live entities appear.
- **Show Abstract Templates** — toggle whether abstract (non-instantiable) templates appear.
- **Help…** — opens the same inline help panel as the `?` button.
- **Clear Search History** — wipes the saved query list.

Both visibility toggles are persisted via `EditorPrefs`.

## Inspector cross-links and hover preview

!!! warning "Screenshot pending: `images/hierarchy-inspector-links.png`"
    Hierarchy on the left, Unity Inspector on the right showing a template's component list with the cross-link buttons visible. Ideally with a `Shift+hover` preview highlight visible on a row in the tree.

<!-- When captured, delete the admonition above and uncomment this block:
<figure markdown>
  ![Hierarchy alongside Unity's Inspector showing cross-link buttons on a template view](images/hierarchy-inspector-links.png){ width="900" }
  <figcaption>Inspector cross-links navigate the hierarchy by stable identity, so selection survives world transitions and domain reloads.</figcaption>
</figure>
-->

Selecting a row drives Unity's standard inspector to a Trecs-aware view of the entity / template / component / set / tag. Component fields are shown as a per-entity component inspector with JSON edit support, and most inspector views include navigation links back to other rows in the hierarchy:

- Clicking a link selects the linked row in the hierarchy (and scrolls it into view).
- Holding `Shift` while hovering a link previews the destination — the hierarchy scrolls but the selection doesn't change, so the inspector keeps showing your current row.

Selection and link navigation persist across domain reloads and across the live ↔ cache transition: rows are identified by stable string keys (e.g. `template:Foo`, `accessor:MoveSystem`), not by transient object references.

## Live vs cache mode

Trecs writes a schema snapshot to `Library/` whenever a world runs in the editor, so the hierarchy still has something to show after Play mode ends or between domain reloads.

| | Live mode | Cache mode |
|---|---|---|
| Source | A live `World` instance | Schema snapshot from `Library/` |
| Refresh cadence | Periodic refresh of counts and system-enabled state | Static — snapshot is read once |
| System-enable toggle | Active | Disabled |
| Entity counts on rows | Live counts | Hidden (cache holds no entity instances) |

The window switches automatically: when a live world appears it takes over the dropdown; when the last live world stops, the most recent compatible snapshot loads in cache mode. The dropdown's `(cached)` suffix is the at-a-glance indicator.

## See also

- [World Setup](../core/world-setup.md) — `WorldBuilder.SetDebugName` controls how a world appears in the dropdown.
- [Disabling and Pausing Systems](../advanced/system-control.md) — `EnableChannel.Editor` is the channel the hierarchy's per-system enable toggle uses.
- Sibling editor windows (also under `Window > Trecs`):
    - **Trecs Player** — record / playback / snapshot / scrub / fork / loop UI for the active world.
    - **Trecs Saves** — library window for managing recordings and snapshots.
