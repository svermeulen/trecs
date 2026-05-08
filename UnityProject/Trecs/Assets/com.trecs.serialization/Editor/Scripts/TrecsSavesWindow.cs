using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Serialization
{
    /// <summary>
    /// Library window for managing all on-disk Trecs saves — both
    /// <em>recordings</em> (time-range scrubbable buffers) and
    /// <em>snapshots</em> (single-frame save-states). Lists both in
    /// labelled sections with per-row Load / Rename / Delete actions and a
    /// search filter.
    ///
    /// This window owns library management (browse, rename, delete,
    /// search). The Player window owns the active recording's transport
    /// and offers convenience Save / Load on its Actions menu for the
    /// common in-flight cases. Saving a recording from disk lives in
    /// Player because it operates on the live in-memory buffer the Player
    /// is showing; capturing a snapshot is available in both surfaces
    /// since it's a one-shot.
    /// </summary>
    public class TrecsSavesWindow : EditorWindow
    {
        DropdownField _worldDropdown;
        ToolbarSearchField _searchField;

        // Capture / Save-As surfaced through per-row Save icons and a
        // trailing "+ Save as new…" row in each section — no top-level
        // button.
        ScrollView _listScroll;
        Label _statusLabel;

        World _selectedWorld;
        readonly List<World> _dropdownWorlds = new();

        IVisualElementScheduledItem _statusClearer;
        const int StatusClearDelayMs = 4000;

        // Once the user opts out via the confirm dialog's "Don't ask again"
        // button, suppress the load-during-recording prompt forever. Stored
        // in EditorPrefs so the choice persists across editor sessions.
        // Same key used by TrecsPlayerWindow's snapshot loader so the
        // setting carries between both surfaces. The key still spells
        // "Bookmarks" — predates the bookmark→snapshot rename. Keeping the
        // original spelling so existing user dismissals carry over.
        const string SuppressLoadConfirmKey = "Trecs.Bookmarks.SuppressLoadConfirm";

        string _searchQuery = string.Empty;

        // Selection state. Identity is (name, isRecording) so the same name
        // in both sections doesn't collide. _anchorItem is the "from" point
        // for shift-range selection — set on every plain or ctrl click.
        readonly HashSet<(string name, bool isRecording)> _selection = new();
        (string name, bool isRecording)? _anchorItem;

        // Cache of parsed recording headers keyed by file path. Invalidated
        // by file mtime so renames / overwrites refresh on next read.
        readonly Dictionary<
            string,
            (DateTime mtime, RecordingHeader header)
        > _recordingHeaderCache = new();

        // Active controller for the selected world, tracked so the loaded-
        // row highlight updates immediately when the recorder's backing
        // file changes (load, save, fork, reset).
        TrecsGameStateController _subscribedController;

        [MenuItem("Window/Trecs/Saves")]
        public static void ShowWindow()
        {
            var window = GetWindow<TrecsSavesWindow>();
            window.titleContent = new GUIContent("Trecs Saves");
            window.minSize = new Vector2(320, 280);
        }

        void OnEnable()
        {
            WorldRegistry.WorldRegistered += OnWorldRegistered;
            WorldRegistry.WorldUnregistered += OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged += OnSharedActiveWorldChanged;
            // Library refreshes when any controller saves / deletes /
            // renames, so we don't have to poll or rely on a re-focus.
            TrecsGameStateController.SavesChanged += OnSavesChanged;
        }

        void OnDisable()
        {
            WorldRegistry.WorldRegistered -= OnWorldRegistered;
            WorldRegistry.WorldUnregistered -= OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged -= OnSharedActiveWorldChanged;
            TrecsGameStateController.SavesChanged -= OnSavesChanged;
            UnsubscribeFromController();
        }

        void RefreshControllerSubscription()
        {
            var controller = GetController();
            if (_subscribedController == controller)
                return;
            UnsubscribeFromController();
            _subscribedController = controller;
            if (_subscribedController != null)
            {
                _subscribedController.LoadedRecordingChanged += OnLoadedRecordingChanged;
            }
        }

        void UnsubscribeFromController()
        {
            if (_subscribedController != null)
            {
                _subscribedController.LoadedRecordingChanged -= OnLoadedRecordingChanged;
                _subscribedController = null;
            }
        }

        void OnLoadedRecordingChanged() => RebuildSavesList();

        void OnSavesChanged() => RebuildSavesList();

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            _worldDropdown = new DropdownField("World", new List<string>(), 0);
            _worldDropdown.tooltip =
                "World whose live state will be captured by Capture, and into "
                + "which a save will be loaded.";
            _worldDropdown.RegisterValueChangedCallback(evt =>
            {
                var idx = _worldDropdown.choices.IndexOf(evt.newValue);
                if (idx >= 0 && idx < _dropdownWorlds.Count)
                {
                    SelectWorld(_dropdownWorlds[idx]);
                }
            });
            root.Add(_worldDropdown);

            // Search field — full row, no companion. Capture / Save As
            // moved into the per-section list (each row gets a Save icon
            // that overwrites that slot, plus a trailing "+ Save as new…"
            // row at the end of each section that prompts for a name).
            // ToolbarSearchField renders the magnifying-glass icon and the
            // clear "x" button itself — adding the equivalent USS class to a
            // plain TextField doesn't, since the icon is a child element built
            // by the control rather than styled by the stylesheet.
            _searchField = new ToolbarSearchField();
            _searchField.style.marginTop = 6;
            // ToolbarSearchField has a fixed USS-default width that blocks
            // the natural cross-axis stretch. Set width = 100% to override
            // it. flexGrow would grow on the parent's main axis (column =
            // vertical), which is not what we want.
            _searchField.style.width = new Length(100, LengthUnit.Percent);
            _searchField.tooltip = "Filter the list by name (case-insensitive substring match).";
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _searchQuery = evt.newValue ?? string.Empty;
                RebuildSavesList();
            });
            root.Add(_searchField);

            _listScroll = new ScrollView(ScrollViewMode.Vertical);
            _listScroll.style.flexGrow = 1;
            _listScroll.style.marginTop = 8;
            _listScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            // Hide the recordings Size column when the list gets narrow.
            // Only rebuild when the threshold is actually crossed — fires
            // on every resize event otherwise, which is wasteful.
            _listScroll.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var width = _listScroll.contentRect.width;
                if (width <= 0)
                    return;
                var shouldShow = width >= HideRecordingSizeBelowWidth;
                if (shouldShow != _showRecordingSizeColumn)
                {
                    _showRecordingSizeColumn = shouldShow;
                    RebuildSavesList();
                }
            });
            root.Add(_listScroll);

            _statusLabel = new Label();
            _statusLabel.style.marginTop = 4;
            _statusLabel.style.opacity = 0.7f;
            root.Add(_statusLabel);

            RebuildWorldDropdown();
            RebuildSavesList();
        }

        void OnWorldRegistered(World _) => RebuildWorldDropdown();

        void OnWorldUnregistered(World _) => RebuildWorldDropdown();

        void OnSharedActiveWorldChanged(World world)
        {
            if (world != _selectedWorld && _dropdownWorlds.Contains(world))
            {
                SelectWorld(world);
                var idx = _dropdownWorlds.IndexOf(world);
                if (idx >= 0 && idx < _worldDropdown.choices.Count)
                {
                    _worldDropdown.SetValueWithoutNotify(_worldDropdown.choices[idx]);
                }
            }
        }

        void RebuildWorldDropdown()
        {
            if (_worldDropdown == null)
                return;
            _dropdownWorlds.Clear();
            var labels = new List<string>();
            var active = WorldRegistry.ActiveWorlds;
            for (int i = 0; i < active.Count; i++)
            {
                _dropdownWorlds.Add(active[i]);
                labels.Add(active[i].DebugName ?? $"World #{i}");
            }
            _worldDropdown.choices = labels;
            // Same single-world hide pattern as the Player window — dropdown
            // is wasted vertical space when there's only one option.
            _worldDropdown.style.display =
                _dropdownWorlds.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_dropdownWorlds.Count == 0)
            {
                SelectWorld(null);
                _worldDropdown.SetValueWithoutNotify(string.Empty);
                return;
            }
            var idx = _selectedWorld == null ? -1 : _dropdownWorlds.IndexOf(_selectedWorld);
            if (idx < 0)
            {
                var shared = TrecsEditorSelection.ActiveWorld;
                idx = shared == null ? 0 : Math.Max(0, _dropdownWorlds.IndexOf(shared));
                SelectWorld(_dropdownWorlds[idx]);
            }
            _worldDropdown.SetValueWithoutNotify(labels[idx]);
        }

        void SelectWorld(World world)
        {
            _selectedWorld = world;
            // Re-bind LoadedRecordingChanged to the new world's controller.
            RefreshControllerSubscription();
            // Per-row action buttons capture the world's enabled state at
            // build time, so a list built before any world was registered
            // would keep its Load/Save buttons greyed out forever after
            // one appeared. Rebuild whenever selection changes.
            RebuildSavesList();
        }

        TrecsGameStateController GetController()
        {
            return _selectedWorld == null
                ? null
                : TrecsGameStateRegistry.GetForWorld(_selectedWorld);
        }

        // ── Capture ──

        void OnCaptureClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetStatus("No active controller for the selected world.");
                return;
            }
            var name = TrecsTextPromptWindow.Prompt(
                "Capture snapshot",
                "Snapshot name:",
                SuggestSnapshotName(),
                this
            );
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            if (File.Exists(TrecsGameStateController.GetSnapshotPath(name)))
            {
                if (
                    !EditorUtility.DisplayDialog(
                        "Overwrite snapshot?",
                        $"A snapshot named '{name}' already exists. Overwrite?",
                        "Overwrite",
                        "Cancel"
                    )
                )
                {
                    return;
                }
            }
            if (controller.SaveSnapshot(name))
            {
                SetStatus($"Captured '{name}'.");
                RebuildSavesList();
            }
            else
            {
                SetStatus($"Capture failed for '{name}'.");
            }
        }

        // ── Per-row actions: Snapshots ──

        void OnLoadSnapshotClicked(string name)
        {
            var controller = GetController();
            if (controller == null)
            {
                SetStatus("No active controller for the selected world.");
                return;
            }
            if (
                controller.AutoRecorder.IsRecording
                && !EditorPrefs.GetBool(SuppressLoadConfirmKey, false)
            )
            {
                // DisplayDialogComplex returns 0=ok, 1=cancel, 2=alt.
                var choice = EditorUtility.DisplayDialogComplex(
                    "Load snapshot?",
                    "Loading a snapshot will discard the current in-memory "
                        + "recording buffer (saved files on disk are not "
                        + "affected) and start a fresh recording from the "
                        + "snapshot's frame.",
                    "Load",
                    "Cancel",
                    "Load and don't ask again"
                );
                if (choice == 1)
                    return;
                if (choice == 2)
                    EditorPrefs.SetBool(SuppressLoadConfirmKey, true);
            }
            if (controller.LoadSnapshot(name))
            {
                SetStatus($"Loaded snapshot '{name}'.");
            }
            else
            {
                SetStatus($"Load failed for '{name}'.");
            }
        }

        void OnRenameSnapshotClicked(string oldName)
        {
            var newName = TrecsTextPromptWindow.Prompt(
                "Rename snapshot",
                "New name:",
                oldName,
                this
            );
            if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
            {
                return;
            }
            if (TrecsGameStateController.RenameSnapshot(oldName, newName))
            {
                SetStatus($"Renamed '{oldName}' → '{newName}'.");
                RebuildSavesList();
            }
            else
            {
                SetStatus("Rename failed (target may already exist).");
            }
        }

        void OnDeleteSnapshotClicked(string name)
        {
            if (
                !EditorUtility.DisplayDialog(
                    "Delete snapshot?",
                    $"Delete snapshot '{name}'? This removes the file from disk.",
                    "Delete",
                    "Cancel"
                )
            )
            {
                return;
            }
            // Static delete is via File.Delete + log; piggyback on controller
            // when available so logs route through the same module logger;
            // fall through to direct File.Delete when no controller is
            // around (e.g. when no scene is playing — file ops don't need a
            // world).
            var controller = GetController();
            var ok =
                controller != null
                    ? controller.DeleteSnapshot(name)
                    : DeleteFile(TrecsGameStateController.GetSnapshotPath(name));
            if (ok)
            {
                SetStatus($"Deleted snapshot '{name}'.");
                RebuildSavesList();
            }
            else
            {
                SetStatus($"Delete failed for '{name}'.");
            }
        }

        // ── Per-row actions: Recordings ──

        void OnLoadRecordingClicked(string name)
        {
            var controller = GetController();
            if (controller == null)
            {
                SetStatus("No active controller for the selected world.");
                return;
            }
            if (controller.LoadNamedRecording(name))
            {
                SetStatus($"Loaded recording '{name}'.");
            }
            else
            {
                SetStatus($"Load failed for '{name}'.");
            }
        }

        void OnRenameRecordingClicked(string oldName)
        {
            var newName = TrecsTextPromptWindow.Prompt(
                "Rename recording",
                "New name:",
                oldName,
                this
            );
            if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
            {
                return;
            }
            if (TrecsGameStateController.RenameNamedRecording(oldName, newName))
            {
                SetStatus($"Renamed '{oldName}' → '{newName}'.");
                RebuildSavesList();
            }
            else
            {
                SetStatus("Rename failed (target may already exist).");
            }
        }

        void OnDeleteRecordingClicked(string name)
        {
            if (
                !EditorUtility.DisplayDialog(
                    "Delete recording?",
                    $"Delete recording '{name}'? This removes the file from disk.",
                    "Delete",
                    "Cancel"
                )
            )
            {
                return;
            }
            var controller = GetController();
            var ok =
                controller != null
                    ? controller.DeleteNamedRecording(name)
                    : DeleteFile(TrecsGameStateController.GetRecordingPath(name));
            if (ok)
            {
                SetStatus($"Deleted recording '{name}'.");
                RebuildSavesList();
            }
            else
            {
                SetStatus($"Delete failed for '{name}'.");
            }
        }

        static bool DeleteFile(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            // Fallback path bypasses the controller, so notify the library
            // event manually so other observers refresh.
            TrecsGameStateController.NotifySavesChanged();
            return true;
        }

        // ── List rendering ──

        void RebuildSavesList()
        {
            if (_listScroll == null)
                return;
            // Preserve scroll position across rebuild — without this, every
            // save / delete / rename / search-keystroke jumps the user back
            // to the top, which is jarring when the list is long.
            var preservedOffset = _listScroll.scrollOffset;
            _listScroll.Clear();

            var allRecordings = TrecsGameStateController.GetSavedRecordingNames();
            var allSnapshots = TrecsGameStateController.GetSavedSnapshotNames();
            var recordings = FilterByQuery(allRecordings);
            var snapshots = FilterByQuery(allSnapshots);

            // Drop any selected items whose files no longer exist (e.g.
            // deleted by another window) so the selection toolbar count
            // matches reality.
            PruneStaleSelection(allRecordings, allSnapshots);

            // Selection toolbar pinned at the top of the list when anything
            // is selected — always visible even when the user has scrolled
            // far down so they can act on the selection without losing place.
            var selectionBar = BuildSelectionToolbar();
            if (selectionBar != null)
                _listScroll.Add(selectionBar);

            // Always render both section blocks so the trailing "+ Save as
            // new…" rows stay reachable even when the library is empty or
            // the filter hides everything. Section bodies handle their own
            // empty/filtered states.

            // Recordings section. Header includes the visible count vs the
            // total when search filters out some — keeps "(0/3)" style
            // discoverable when a query hides all matches in a section.
            _listScroll.Add(
                BuildSectionHeader(
                    "Recordings",
                    "Animation.Record",
                    recordings.Count,
                    allRecordings.Count
                )
            );
            if (allRecordings.Count == 0)
            {
                _listScroll.Add(
                    BuildEmptySectionRow("none yet — use the row below to save the current buffer")
                );
            }
            else if (recordings.Count == 0)
            {
                _listScroll.Add(BuildEmptySectionRow("none match the filter"));
            }
            else
            {
                _listScroll.Add(BuildColumnHeader(isRecording: true));
                foreach (var name in recordings)
                {
                    _listScroll.Add(BuildSaveRow(name, isRecording: true));
                }
            }
            // Trailing "+ Save as new…" row sits at the end of the section,
            // even when filtered to zero — it's a write affordance, not
            // filtered data.
            _listScroll.Add(BuildSaveAsNewRow(isRecording: true));

            _listScroll.Add(
                BuildSectionHeader(
                    "Snapshots",
                    "SceneViewCamera",
                    snapshots.Count,
                    allSnapshots.Count
                )
            );
            if (allSnapshots.Count == 0)
            {
                _listScroll.Add(
                    BuildEmptySectionRow("none yet — use the row below to capture one")
                );
            }
            else if (snapshots.Count == 0)
            {
                _listScroll.Add(BuildEmptySectionRow("none match the filter"));
            }
            else
            {
                _listScroll.Add(BuildColumnHeader(isRecording: false));
                foreach (var name in snapshots)
                {
                    _listScroll.Add(BuildSaveRow(name, isRecording: false));
                }
            }
            _listScroll.Add(BuildSaveAsNewRow(isRecording: false));

            // Restore scroll after layout has had a chance to size the new
            // children — assigning before children are laid out can clamp
            // the offset to 0. Schedule a single trailing pass.
            _listScroll
                .schedule.Execute(() => _listScroll.scrollOffset = preservedOffset)
                .ExecuteLater(0);
        }

        List<string> FilterByQuery(IReadOnlyList<string> names)
        {
            var result = new List<string>();
            if (names == null)
                return result;
            if (string.IsNullOrEmpty(_searchQuery))
            {
                for (int i = 0; i < names.Count; i++)
                    result.Add(names[i]);
                return result;
            }
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(names[i]);
                }
            }
            return result;
        }

        VisualElement BuildSectionHeader(
            string title,
            string iconName,
            int visibleCount,
            int totalCount
        )
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 8;
            row.style.marginBottom = 2;

            var icon = ResolveEditorIcon(iconName);
            if (icon != null)
            {
                var iconElement = new VisualElement();
                iconElement.style.width = 14;
                iconElement.style.height = 14;
                iconElement.style.marginRight = 5;
                iconElement.style.flexShrink = 0;
                iconElement.style.backgroundImage = new StyleBackground(icon);
                row.Add(iconElement);
            }

            var label = new Label(
                visibleCount == totalCount
                    ? $"{title} ({totalCount})"
                    : $"{title} ({visibleCount}/{totalCount})"
            );
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(label);
            return row;
        }

        VisualElement BuildEmptySectionRow(string text)
        {
            var label = new Label($"({text})");
            label.style.unityFontStyleAndWeight = FontStyle.Italic;
            label.style.opacity = 0.55f;
            label.style.marginLeft = 6;
            label.style.marginBottom = 2;
            return label;
        }

        VisualElement BuildColumnHeader(bool isRecording)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 2;
            row.style.paddingBottom = 2;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new StyleColor(new Color(1, 1, 1, 0.15f));
            row.style.marginBottom = 2;
            // Leading inset matches the row-level paddingLeft we use to align
            // labels with selectable content (see BuildSaveRow).
            row.style.paddingLeft = RowPaddingLeft;
            row.Add(MakeHeaderLabel("Name", flexGrow: 1));
            if (isRecording)
            {
                row.Add(MakeHeaderLabel("Duration", fixedWidth: DurationColumnWidth));
                if (_showRecordingSizeColumn)
                    row.Add(MakeHeaderLabel("Size", fixedWidth: SizeColumnWidth));
            }
            else
            {
                row.Add(MakeHeaderLabel("Size", fixedWidth: SizeColumnWidth));
            }
            row.Add(MakeHeaderLabel("Saved", fixedWidth: TimeColumnWidth));
            // Spacer matches the per-row action-button cluster width so the
            // header columns line up with the data rows even though the
            // header has no buttons.
            var actionsSpacer = new VisualElement();
            actionsSpacer.style.width = ActionsColumnWidth;
            row.Add(actionsSpacer);
            return row;
        }

        static Label MakeHeaderLabel(string text, float flexGrow = 0, float fixedWidth = 0)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.opacity = 0.7f;
            if (flexGrow > 0)
                label.style.flexGrow = flexGrow;
            if (fixedWidth > 0)
            {
                label.style.width = fixedWidth;
                label.style.flexShrink = 0;
            }
            return label;
        }

        const float SizeColumnWidth = 60;
        const float DurationColumnWidth = 70;
        const float TimeColumnWidth = 80;
        const float RowPaddingLeft = 6;

        // Recordings show both Duration and Size columns when there's room.
        // Below this content width the Size column hides — Duration is the
        // more useful measure for picking a recording to load, and Size
        // remains visible in the cell tooltip. Snapshots only ever have a
        // Size column and never hide it. Threshold sized so Name still gets
        // ~80 px of room with both columns visible at the chosen width.
        const float HideRecordingSizeBelowWidth = 360;
        bool _showRecordingSizeColumn = true;

        // Icon-only Play + kebab buttons. 18px matches Unity's editor-toolbar
        // baseline. Kebab opens a GenericMenu with the secondary actions
        // (Save-into-slot, Rename, Reveal in Finder, Delete) — keeping the
        // row visually quiet while leaving everything one click away.
        const float IconButtonSize = 18;
        const float ActionsColumnWidth = IconButtonSize * 2 + 2 + 4;

        static Button MakeIconButton(
            string iconName,
            string tooltip,
            Action onClick,
            bool enabled = true,
            string fallbackText = null
        )
        {
            var btn = new Button(onClick);
            btn.tooltip = tooltip;
            btn.style.width = IconButtonSize;
            btn.style.height = IconButtonSize;
            btn.style.paddingLeft = 0;
            btn.style.paddingRight = 0;
            btn.style.paddingTop = 0;
            btn.style.paddingBottom = 0;
            var icon = ResolveEditorIcon(iconName);
            if (icon != null)
            {
                btn.style.backgroundImage = new StyleBackground(icon);
                // UI Toolkit's default :disabled styling doesn't reliably
                // tint backgroundImage, so dim explicitly when disabled.
                if (!enabled)
                {
                    btn.style.unityBackgroundImageTintColor = new Color(1f, 1f, 1f, 0.4f);
                }
            }
            else if (!string.IsNullOrEmpty(fallbackText))
            {
                btn.text = fallbackText;
            }
            btn.SetEnabled(enabled);
            return btn;
        }

        // Pro skin uses `d_`-prefixed variants for many built-in editor
        // icons; fall back to the unprefixed asset when the dark variant
        // is missing (e.g. TreeEditor.Trash has no d_ counterpart).
        //
        // FindTexture (vs IconContent) returns null silently when the icon
        // isn't found — IconContent logs a console warning and returns a
        // missing-icon placeholder, which is noisy and impossible to detect
        // post-hoc since the placeholder texture is non-null.
        static Texture2D ResolveEditorIcon(string name)
        {
            if (EditorGUIUtility.isProSkin)
            {
                var dark = EditorGUIUtility.FindTexture("d_" + name);
                if (dark != null)
                    return dark;
            }
            return EditorGUIUtility.FindTexture(name);
        }

        VisualElement BuildSaveRow(string name, bool isRecording)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 2;
            row.style.paddingBottom = 2;
            row.style.paddingLeft = RowPaddingLeft;

            // Three orthogonal background states: hover, selection, and
            // "loaded in Player" (recordings only). Selection wins over the
            // loaded marker; hover only shows when neither overrides it.
            var key = (name, isRecording);
            var isSelected = _selection.Contains(key);
            // Highlight the row backing the recording currently open in the
            // Player so users can spot it at a glance. Snapshots have no
            // "currently loaded" concept — they're applied as one-shot
            // jumps — so only recordings get the marker.
            var isLoadedInPlayer =
                isRecording
                && string.Equals(
                    name,
                    GetController()?.LoadedRecordingName,
                    StringComparison.Ordinal
                );
            ApplyRowBackground(row, isSelected, isLoadedInPlayer, isHover: false);
            row.RegisterCallback<MouseEnterEvent>(_ =>
                ApplyRowBackground(row, _selection.Contains(key), isLoadedInPlayer, isHover: true)
            );
            row.RegisterCallback<MouseLeaveEvent>(_ =>
                ApplyRowBackground(row, _selection.Contains(key), isLoadedInPlayer, isHover: false)
            );

            // Selection / context-menu mouse handler on the row body. We
            // ignore clicks landing on the inline buttons because button
            // presses already handle their own actions and shouldn't change
            // the selection. MouseDownEvent (not Click) so shift/ctrl
            // modifiers come through reliably and so the selection feels
            // immediate.
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                // Click landed on (or inside) one of the inline buttons —
                // the button's own handler covers it; don't also toggle
                // selection. Walk parents because the target may be a
                // child element of the Button (icon, label).
                if (evt.target is VisualElement target && IsInsideButton(target, row))
                    return;
                if (evt.button == 1) // right-click — open the row menu
                {
                    OpenRowMenu(name, isRecording, anchorRect: null);
                    evt.StopPropagation();
                    return;
                }
                if (evt.button != 0)
                    return;
                HandleRowClick(name, isRecording, evt.shiftKey, evt.ctrlKey || evt.commandKey);
                evt.StopPropagation();
            });

            var nameLabel = new Label(name);
            nameLabel.style.flexGrow = 1;
            nameLabel.style.flexShrink = 1;
            nameLabel.style.minWidth = 0;
            nameLabel.style.overflow = Overflow.Hidden;
            nameLabel.style.textOverflow = TextOverflow.Ellipsis;
            nameLabel.style.whiteSpace = WhiteSpace.NoWrap;
            nameLabel.tooltip = isLoadedInPlayer ? $"{name}  (currently loaded in Player)" : name;
            if (isLoadedInPlayer)
            {
                nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            row.Add(nameLabel);

            // FileInfo throws on missing/locked files but the file definitely
            // exists (we just listed the directory) — fall back to placeholders
            // for the size/time columns rather than skipping the row entirely.
            long size = -1;
            DateTime savedAt = default;
            string path = null;
            try
            {
                path = isRecording
                    ? TrecsGameStateController.GetRecordingPath(name)
                    : TrecsGameStateController.GetSnapshotPath(name);
                var info = new FileInfo(path);
                size = info.Length;
                savedAt = info.LastWriteTime;
            }
            catch
            {
                // leave defaults; columns render as "—".
            }

            // Recordings show duration (frames * fixedDeltaTime, parsed from
            // the file header) since that's what users care about when
            // picking a recording to load — plus a Size column when the
            // window is wide enough. Snapshots are single-frame so they
            // show only Size.
            if (isRecording)
            {
                string durationText = "—";
                string durationTooltip = null;
                if (path != null && TryGetCachedRecordingHeader(path, out var header))
                {
                    durationText = FormatDuration(header.DurationSeconds);
                    durationTooltip =
                        $"{header.FrameCount} frames @ {1f / Math.Max(header.FixedDeltaTime, 0.0001f):0.#} Hz";
                }
                var durationLabel = new Label(durationText);
                durationLabel.style.width = DurationColumnWidth;
                durationLabel.style.flexShrink = 0;
                durationLabel.style.opacity = 0.8f;
                if (durationTooltip != null)
                    durationLabel.tooltip = durationTooltip;
                row.Add(durationLabel);

                if (_showRecordingSizeColumn)
                {
                    var sizeLabel = new Label(size >= 0 ? FormatSize(size) : "—");
                    sizeLabel.style.width = SizeColumnWidth;
                    sizeLabel.style.flexShrink = 0;
                    sizeLabel.style.opacity = 0.8f;
                    row.Add(sizeLabel);
                }
            }
            else
            {
                var sizeLabel = new Label(size >= 0 ? FormatSize(size) : "—");
                sizeLabel.style.width = SizeColumnWidth;
                sizeLabel.style.flexShrink = 0;
                sizeLabel.style.opacity = 0.8f;
                row.Add(sizeLabel);
            }

            var timeLabel = new Label(savedAt != default ? FormatRelativeTime(savedAt) : "—");
            timeLabel.style.width = TimeColumnWidth;
            timeLabel.style.flexShrink = 0;
            timeLabel.style.opacity = 0.8f;
            if (savedAt != default)
            {
                timeLabel.tooltip = savedAt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            row.Add(timeLabel);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.width = ActionsColumnWidth;
            actions.style.flexShrink = 0;
            actions.style.justifyContent = Justify.FlexEnd;

            var hasWorld = _selectedWorld != null && !_selectedWorld.IsDisposed;

            var loadBtn = MakeIconButton(
                "PlayButton",
                "Load",
                isRecording
                    ? (Action)(() => OnLoadRecordingClicked(name))
                    : (Action)(() => OnLoadSnapshotClicked(name)),
                hasWorld
            );
            actions.Add(loadBtn);

            Button menuBtn = null;
            menuBtn = MakeIconButton(
                "_Menu",
                "More actions…",
                () => OpenRowMenu(name, isRecording, anchorRect: menuBtn?.worldBound),
                fallbackText: "⋮"
            );
            menuBtn.style.marginLeft = 2;
            actions.Add(menuBtn);

            row.Add(actions);
            return row;
        }

        // Resolves the three-axis row background (selection / loaded-in-player /
        // hover) into a single background color. Selection wins; loaded marker
        // shows under selection via the left border only; hover applies only
        // when neither structural state is set.
        static void ApplyRowBackground(
            VisualElement row,
            bool isSelected,
            bool isLoadedInPlayer,
            bool isHover
        )
        {
            if (isSelected)
            {
                row.style.backgroundColor = new Color(0.2f, 0.5f, 0.85f, 0.55f);
            }
            else if (isLoadedInPlayer)
            {
                row.style.backgroundColor = new Color(0.15f, 0.35f, 0.6f, 0.35f);
            }
            else if (isHover)
            {
                row.style.backgroundColor = new Color(1f, 1f, 1f, 0.05f);
            }
            else
            {
                // Fully transparent rather than StyleKeyword.Null so the
                // assignment is explicit and theme-independent.
                row.style.backgroundColor = new Color(0, 0, 0, 0);
            }

            // The left accent stripe stays for the loaded recording so users
            // can still spot it after selecting it (selection only changes
            // the fill colour).
            if (isLoadedInPlayer)
            {
                row.style.borderLeftWidth = 2;
                row.style.borderLeftColor = new StyleColor(new Color(0.4f, 0.7f, 1f));
            }
            else
            {
                row.style.borderLeftWidth = 0;
            }
        }

        // Walk the target's ancestor chain up to (but not including) the
        // row, returning true if any ancestor is a Button. UI Toolkit's
        // hit-testing reports the deepest pickable element, which is often
        // a child of the Button (its inner Label or background image), not
        // the Button itself.
        static bool IsInsideButton(VisualElement target, VisualElement stopAt)
        {
            for (var cur = target; cur != null && cur != stopAt; cur = cur.parent)
            {
                if (cur is Button)
                    return true;
            }
            return false;
        }

        void HandleRowClick(string name, bool isRecording, bool shift, bool ctrl)
        {
            var key = (name, isRecording);
            if (shift && _anchorItem.HasValue && _anchorItem.Value.isRecording == isRecording)
            {
                // Range-select within the same section using the current
                // (filtered) ordering — what the user sees is what they
                // select.
                var names = isRecording
                    ? FilterByQuery(TrecsGameStateController.GetSavedRecordingNames())
                    : FilterByQuery(TrecsGameStateController.GetSavedSnapshotNames());
                var anchorIdx = names.IndexOf(_anchorItem.Value.name);
                var clickIdx = names.IndexOf(name);
                if (anchorIdx >= 0 && clickIdx >= 0)
                {
                    if (!ctrl)
                        _selection.Clear();
                    var lo = Math.Min(anchorIdx, clickIdx);
                    var hi = Math.Max(anchorIdx, clickIdx);
                    for (int i = lo; i <= hi; i++)
                        _selection.Add((names[i], isRecording));
                    RebuildSavesList();
                    return;
                }
            }

            if (ctrl)
            {
                if (!_selection.Remove(key))
                    _selection.Add(key);
            }
            else
            {
                _selection.Clear();
                _selection.Add(key);
            }
            _anchorItem = key;
            RebuildSavesList();
        }

        void OpenRowMenu(string name, bool isRecording, Rect? anchorRect)
        {
            var menu = new GenericMenu();
            var hasWorld = _selectedWorld != null && !_selectedWorld.IsDisposed;
            var saveEnabled = hasWorld && (!isRecording || HasRecordingBuffer());

            var saveLabel = isRecording
                ? "Save current recording into this slot"
                : "Capture current frame into this snapshot";
            if (saveEnabled)
            {
                menu.AddItem(
                    new GUIContent(saveLabel),
                    false,
                    () =>
                    {
                        if (isRecording)
                            OnOverwriteRecordingClicked(name);
                        else
                            OnOverwriteSnapshotClicked(name);
                    }
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(saveLabel));
            }

            menu.AddItem(
                new GUIContent("Rename…"),
                false,
                () =>
                {
                    if (isRecording)
                        OnRenameRecordingClicked(name);
                    else
                        OnRenameSnapshotClicked(name);
                }
            );

            menu.AddItem(
                new GUIContent("Reveal in Finder"),
                false,
                () => OnRevealInFinder(name, isRecording)
            );

            menu.AddSeparator(string.Empty);

            menu.AddItem(
                new GUIContent("Delete"),
                false,
                () =>
                {
                    if (isRecording)
                        OnDeleteRecordingClicked(name);
                    else
                        OnDeleteSnapshotClicked(name);
                }
            );

            if (anchorRect.HasValue)
            {
                // DropDown wants a rect in panel coordinates; worldBound on
                // the kebab button gives us exactly that.
                menu.DropDown(anchorRect.Value);
            }
            else
            {
                // Right-click path — show at cursor via the IMGUI context.
                menu.ShowAsContext();
            }
        }

        void OnRevealInFinder(string name, bool isRecording)
        {
            var path = isRecording
                ? TrecsGameStateController.GetRecordingPath(name)
                : TrecsGameStateController.GetSnapshotPath(name);
            if (!File.Exists(path))
            {
                SetStatus("File not found.");
                return;
            }
            EditorUtility.RevealInFinder(path);
        }

        // True iff the live recorder has a non-empty in-memory buffer that
        // could be saved as a recording. Snapshots only need a live world.
        bool HasRecordingBuffer()
        {
            var recorder = GetController()?.AutoRecorder;
            return recorder != null && recorder.IsRecording && recorder.Anchors.Count > 0;
        }

        // Trailing "Save as new" row, rendered as a flat full-width button
        // so the affordance reads clearly as an action rather than a
        // disabled/placeholder row.
        VisualElement BuildSaveAsNewRow(bool isRecording)
        {
            var hasWorld = _selectedWorld != null && !_selectedWorld.IsDisposed;
            var enabled = hasWorld && (!isRecording || HasRecordingBuffer());

            var btn = new Button(
                isRecording
                    ? (Action)OnSaveRecordingAsNewClicked
                    : (Action)OnSaveSnapshotAsNewClicked
            )
            {
                text = isRecording
                    ? "+  Save current recording as new…"
                    : "+  Capture snapshot as new…",
            };
            btn.style.marginTop = 4;
            btn.style.marginLeft = 0;
            btn.style.marginRight = 0;
            btn.style.marginBottom = 2;
            btn.style.paddingTop = 4;
            btn.style.paddingBottom = 4;
            btn.style.unityTextAlign = TextAnchor.MiddleLeft;
            btn.style.paddingLeft = RowPaddingLeft;
            btn.tooltip = isRecording
                ? "Save the current in-memory recording under a new name."
                : "Capture the current frame as a new named snapshot.";
            btn.SetEnabled(enabled);
            return btn;
        }

        // ── Selection / bulk actions ──

        VisualElement BuildSelectionToolbar()
        {
            if (_selection.Count == 0)
                return null;
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingTop = 4;
            bar.style.paddingBottom = 4;
            bar.style.paddingLeft = RowPaddingLeft;
            bar.style.paddingRight = 4;
            bar.style.marginBottom = 2;
            bar.style.backgroundColor = new Color(0.2f, 0.5f, 0.85f, 0.25f);
            bar.style.borderTopLeftRadius = 3;
            bar.style.borderTopRightRadius = 3;
            bar.style.borderBottomLeftRadius = 3;
            bar.style.borderBottomRightRadius = 3;

            var label = new Label(
                _selection.Count == 1 ? "1 selected" : $"{_selection.Count} selected"
            );
            label.style.flexGrow = 1;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            bar.Add(label);

            var deleteBtn = new Button(OnDeleteSelected) { text = "Delete" };
            deleteBtn.style.marginLeft = 4;
            bar.Add(deleteBtn);

            var clearBtn = new Button(() =>
            {
                _selection.Clear();
                _anchorItem = null;
                RebuildSavesList();
            })
            {
                text = "Clear",
            };
            clearBtn.style.marginLeft = 2;
            bar.Add(clearBtn);

            return bar;
        }

        void OnDeleteSelected()
        {
            if (_selection.Count == 0)
                return;
            var snapshotsCount = 0;
            var recordingsCount = 0;
            foreach (var item in _selection)
            {
                if (item.isRecording)
                    recordingsCount++;
                else
                    snapshotsCount++;
            }
            string what =
                recordingsCount > 0 && snapshotsCount > 0
                    ? $"{recordingsCount} recording(s) and {snapshotsCount} snapshot(s)"
                : recordingsCount > 0 ? $"{recordingsCount} recording(s)"
                : $"{snapshotsCount} snapshot(s)";
            if (
                !EditorUtility.DisplayDialog(
                    "Delete selected saves?",
                    $"Delete {what}? This removes the file(s) from disk.",
                    "Delete",
                    "Cancel"
                )
            )
            {
                return;
            }
            // Snapshot the set; mutating during iteration would throw, and a
            // failure mid-loop should still let later items try.
            var items = new List<(string name, bool isRecording)>(_selection);
            _selection.Clear();
            _anchorItem = null;
            var controller = GetController();
            int deleted = 0;
            int failed = 0;
            foreach (var item in items)
            {
                bool ok;
                if (item.isRecording)
                {
                    ok =
                        controller != null
                            ? controller.DeleteNamedRecording(item.name)
                            : DeleteFile(TrecsGameStateController.GetRecordingPath(item.name));
                }
                else
                {
                    ok =
                        controller != null
                            ? controller.DeleteSnapshot(item.name)
                            : DeleteFile(TrecsGameStateController.GetSnapshotPath(item.name));
                }
                if (ok)
                    deleted++;
                else
                    failed++;
            }
            SetStatus(
                failed == 0 ? $"Deleted {deleted} item(s)." : $"Deleted {deleted}; {failed} failed."
            );
            RebuildSavesList();
        }

        // ── Save-as-new handlers ──

        void OnSaveSnapshotAsNewClicked()
        {
            // Same prompt+overwrite-confirm flow as the old top-level
            // Capture button.
            OnCaptureClicked();
        }

        void OnSaveRecordingAsNewClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetStatus("No active controller for the selected world.");
                return;
            }
            if (!HasRecordingBuffer())
            {
                SetStatus("Nothing to save — recorder has no in-memory buffer.");
                return;
            }
            var name = TrecsTextPromptWindow.Prompt(
                "Save recording as new",
                "Recording name:",
                $"recording-{DateTime.Now:yyyyMMdd-HHmmss}",
                this
            );
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            name = name.Trim();
            if (File.Exists(TrecsGameStateController.GetRecordingPath(name)))
            {
                if (
                    !EditorUtility.DisplayDialog(
                        "Overwrite recording?",
                        $"A recording named '{name}' already exists. Overwrite?",
                        "Overwrite",
                        "Cancel"
                    )
                )
                {
                    return;
                }
            }
            if (controller.SaveNamedRecording(name))
            {
                SetStatus($"Saved recording '{name}'.");
            }
            else
            {
                SetStatus($"Save failed for '{name}'.");
            }
        }

        // ── Per-row overwrite (Save-into-this-slot) handlers ──

        void OnOverwriteRecordingClicked(string name)
        {
            var controller = GetController();
            if (controller == null)
            {
                SetStatus("No active controller for the selected world.");
                return;
            }
            if (!HasRecordingBuffer())
            {
                SetStatus("Nothing to save — recorder has no in-memory buffer.");
                return;
            }
            if (
                !EditorUtility.DisplayDialog(
                    "Overwrite recording?",
                    $"Overwrite '{name}' with the current in-memory recording? "
                        + "The existing file on disk will be replaced.",
                    "Overwrite",
                    "Cancel"
                )
            )
            {
                return;
            }
            if (controller.SaveNamedRecording(name))
            {
                SetStatus($"Overwrote recording '{name}'.");
            }
            else
            {
                SetStatus($"Save failed for '{name}'.");
            }
        }

        void OnOverwriteSnapshotClicked(string name)
        {
            var controller = GetController();
            if (controller == null)
            {
                SetStatus("No active controller for the selected world.");
                return;
            }
            if (
                !EditorUtility.DisplayDialog(
                    "Overwrite snapshot?",
                    $"Overwrite '{name}' with the current frame? "
                        + "The existing file on disk will be replaced.",
                    "Overwrite",
                    "Cancel"
                )
            )
            {
                return;
            }
            if (controller.SaveSnapshot(name))
            {
                SetStatus($"Overwrote snapshot '{name}'.");
            }
            else
            {
                SetStatus($"Save failed for '{name}'.");
            }
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024):0.#} MB";
        }

        static string FormatDuration(float seconds)
        {
            if (seconds < 1f)
                return $"{seconds * 1000f:0} ms";
            if (seconds < 60f)
                return $"{seconds:0.0} s";
            int totalSeconds = (int)seconds;
            int minutes = totalSeconds / 60;
            int s = totalSeconds % 60;
            return minutes < 60 ? $"{minutes}m {s:00}s" : $"{minutes / 60}h {minutes % 60:00}m";
        }

        bool TryGetCachedRecordingHeader(string path, out RecordingHeader header)
        {
            try
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                if (_recordingHeaderCache.TryGetValue(path, out var entry) && entry.mtime == mtime)
                {
                    header = entry.header;
                    return true;
                }
                if (TrecsAutoRecorder.TryReadRecordingHeader(path, out header))
                {
                    _recordingHeaderCache[path] = (mtime, header);
                    return true;
                }
            }
            catch
            {
                // file vanished between listing and read — fall through.
            }
            header = default;
            return false;
        }

        // Drop selection entries whose underlying files no longer exist
        // (deleted, renamed, or filtered out). Called from RebuildSavesList
        // so the selection toolbar count always matches what's actually
        // selectable.
        void PruneStaleSelection(
            IReadOnlyList<string> recordingNames,
            IReadOnlyList<string> snapshotNames
        )
        {
            if (_selection.Count == 0)
                return;
            var validRecordings = new HashSet<string>(recordingNames);
            var validSnapshots = new HashSet<string>(snapshotNames);
            _selection.RemoveWhere(item =>
                item.isRecording
                    ? !validRecordings.Contains(item.name)
                    : !validSnapshots.Contains(item.name)
            );
            if (
                _anchorItem.HasValue
                && (
                    _anchorItem.Value.isRecording
                        ? !validRecordings.Contains(_anchorItem.Value.name)
                        : !validSnapshots.Contains(_anchorItem.Value.name)
                )
            )
            {
                _anchorItem = null;
            }
        }

        static string FormatRelativeTime(DateTime when)
        {
            var ago = DateTime.Now - when;
            if (ago < TimeSpan.Zero)
                return when.ToString("MMM d");
            if (ago < TimeSpan.FromSeconds(60))
                return "just now";
            if (ago < TimeSpan.FromMinutes(60))
                return $"{(int)ago.TotalMinutes}m ago";
            if (ago < TimeSpan.FromHours(24))
                return $"{(int)ago.TotalHours}h ago";
            if (ago < TimeSpan.FromDays(7))
                return $"{(int)ago.TotalDays}d ago";
            return when.ToString("MMM d");
        }

        static string SuggestSnapshotName() => $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}";

        void SetStatus(string text)
        {
            if (_statusLabel == null)
                return;
            _statusLabel.text = text;
            _statusClearer?.Pause();
            if (string.IsNullOrEmpty(text))
                return;
            _statusClearer = _statusLabel
                .schedule.Execute(() =>
                {
                    if (_statusLabel != null)
                        _statusLabel.text = string.Empty;
                })
                .StartingIn(StatusClearDelayMs);
        }
    }
}
