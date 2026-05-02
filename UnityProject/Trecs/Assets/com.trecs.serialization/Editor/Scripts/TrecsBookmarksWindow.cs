using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Serialization
{
    /// <summary>
    /// Library window for managing standalone <em>snapshots</em> — single-frame
    /// save-states distinct from recordings. Snapshots are useful as QA repro
    /// fixtures or "revert here later" pins; capturing one does not disturb an
    /// active auto-recording, but loading one stops it (the world's frame
    /// jump would otherwise corrupt the recording's continuity).
    ///
    /// This window is deliberately scoped to *managing the library* — listing,
    /// renaming, deleting, capturing, loading. The Replay window remains the
    /// place to scrub and edit a specific recording.
    /// </summary>
    public class TrecsBookmarksWindow : EditorWindow
    {
        DropdownField _worldDropdown;
        Button _captureButton;
        ScrollView _listScroll;
        Label _statusLabel;

        World _selectedWorld;
        readonly List<World> _dropdownWorlds = new();

        IVisualElementScheduledItem _statusClearer;
        const int StatusClearDelayMs = 4000;

        [MenuItem("Window/Trecs/Bookmarks")]
        public static void ShowWindow()
        {
            var window = GetWindow<TrecsBookmarksWindow>();
            window.titleContent = new GUIContent("Trecs Bookmarks");
            window.minSize = new Vector2(320, 240);
        }

        void OnEnable()
        {
            WorldRegistry.WorldRegistered += OnWorldRegistered;
            WorldRegistry.WorldUnregistered += OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged += OnSharedActiveWorldChanged;
        }

        void OnDisable()
        {
            WorldRegistry.WorldRegistered -= OnWorldRegistered;
            WorldRegistry.WorldUnregistered -= OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged -= OnSharedActiveWorldChanged;
        }

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
                + "which a snapshot will be loaded.";
            _worldDropdown.RegisterValueChangedCallback(evt =>
            {
                var idx = _worldDropdown.choices.IndexOf(evt.newValue);
                if (idx >= 0 && idx < _dropdownWorlds.Count)
                {
                    SelectWorld(_dropdownWorlds[idx]);
                }
            });
            root.Add(_worldDropdown);

            var captureRow = new VisualElement();
            captureRow.style.flexDirection = FlexDirection.Row;
            captureRow.style.marginTop = 6;
            _captureButton = new Button(OnCaptureClicked) { text = "Capture snapshot…" };
            _captureButton.tooltip =
                "Save the selected world's current frame as a standalone "
                + "snapshot. Works in any mode — does not disturb an active "
                + "auto-recording.";
            _captureButton.style.flexGrow = 1;
            captureRow.Add(_captureButton);
            root.Add(captureRow);

            var listHeader = new Label("Saved snapshots");
            listHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            listHeader.style.marginTop = 10;
            listHeader.style.marginBottom = 4;
            root.Add(listHeader);

            _listScroll = new ScrollView(ScrollViewMode.Vertical);
            _listScroll.style.flexGrow = 1;
            _listScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            root.Add(_listScroll);

            _statusLabel = new Label();
            _statusLabel.style.marginTop = 4;
            _statusLabel.style.opacity = 0.7f;
            root.Add(_statusLabel);

            RebuildWorldDropdown();
            RebuildSnapshotList();
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
            // Same single-world hide pattern as the Replay window — dropdown is
            // wasted vertical space when there's only one option.
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
            UpdateActionEnabledStates();
            // Per-row Load buttons capture the world's enabled state at
            // build time, so a list built before any world was registered
            // would keep its Load buttons greyed out forever after one
            // appeared. Rebuild whenever selection changes.
            RebuildSnapshotList();
        }

        void UpdateActionEnabledStates()
        {
            var hasWorld = _selectedWorld != null && !_selectedWorld.IsDisposed;
            _captureButton?.SetEnabled(hasWorld);
        }

        TrecsGameStateController GetController()
        {
            return _selectedWorld == null
                ? null
                : TrecsGameStateRegistry.GetForWorld(_selectedWorld);
        }

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
                RebuildSnapshotList();
            }
            else
            {
                SetStatus($"Capture failed for '{name}'.");
            }
        }

        void OnLoadClicked(string name)
        {
            var controller = GetController();
            if (controller == null)
            {
                SetStatus("No active controller for the selected world.");
                return;
            }
            if (
                controller.AutoRecorder.IsRecording
                && !EditorUtility.DisplayDialog(
                    "Load snapshot?",
                    "Loading a snapshot will discard the current in-memory "
                        + "recording buffer (saved files on disk are not "
                        + "affected) and start a fresh recording from the "
                        + "snapshot's frame. Continue?",
                    "Load",
                    "Cancel"
                )
            )
            {
                return;
            }
            if (controller.LoadSnapshot(name))
            {
                SetStatus($"Loaded '{name}'.");
            }
            else
            {
                SetStatus($"Load failed for '{name}'.");
            }
        }

        void OnRenameClicked(string oldName)
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
                RebuildSnapshotList();
            }
            else
            {
                SetStatus($"Rename failed (target may already exist).");
            }
        }

        void OnDeleteClicked(string name)
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
            // when available so logs route through the same module logger; fall
            // through to direct File.Delete when no controller is around (e.g.
            // when no scene is playing — file ops don't need a world).
            var controller = GetController();
            var ok =
                controller != null ? controller.DeleteSnapshot(name) : DeleteSnapshotFile(name);
            if (ok)
            {
                SetStatus($"Deleted '{name}'.");
                RebuildSnapshotList();
            }
            else
            {
                SetStatus($"Delete failed for '{name}'.");
            }
        }

        static bool DeleteSnapshotFile(string name)
        {
            var path = TrecsGameStateController.GetSnapshotPath(name);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }

        void RebuildSnapshotList()
        {
            if (_listScroll == null)
                return;
            _listScroll.Clear();
            var names = TrecsGameStateController.GetSavedSnapshotNames();
            if (names.Count == 0)
            {
                var empty = new Label("(no snapshots — capture one above)");
                empty.style.unityFontStyleAndWeight = FontStyle.Italic;
                empty.style.opacity = 0.6f;
                empty.style.marginTop = 4;
                _listScroll.Add(empty);
                return;
            }
            _listScroll.Add(BuildHeaderRow());
            foreach (var name in names)
            {
                _listScroll.Add(BuildSnapshotRow(name));
            }
        }

        VisualElement BuildHeaderRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 2;
            row.style.paddingBottom = 2;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new StyleColor(new Color(1, 1, 1, 0.15f));
            row.style.marginBottom = 2;
            row.Add(MakeHeaderLabel("Name", flexGrow: 1));
            row.Add(MakeHeaderLabel("Size", fixedWidth: SizeColumnWidth));
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
        const float TimeColumnWidth = 80;

        // 3 buttons × ~62px each + margins; widened slightly so the longest
        // ("Rename") is never clipped at the editor's default font size.
        const float ActionsColumnWidth = 200;

        VisualElement BuildSnapshotRow(string name)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 2;
            row.style.paddingBottom = 2;

            var nameLabel = new Label(name);
            nameLabel.style.flexGrow = 1;
            nameLabel.style.flexShrink = 1;
            nameLabel.style.minWidth = 0;
            nameLabel.style.overflow = Overflow.Hidden;
            nameLabel.style.textOverflow = TextOverflow.Ellipsis;
            nameLabel.style.whiteSpace = WhiteSpace.NoWrap;
            nameLabel.tooltip = name;
            row.Add(nameLabel);

            // FileInfo throws on missing/locked files but the file definitely
            // exists (we just listed the directory) — fall back to placeholders
            // for the size/time columns rather than skipping the row entirely.
            long size = -1;
            DateTime savedAt = default;
            try
            {
                var info = new FileInfo(TrecsGameStateController.GetSnapshotPath(name));
                size = info.Length;
                savedAt = info.LastWriteTime;
            }
            catch
            {
                // leave defaults; columns render as "—".
            }

            var sizeLabel = new Label(size >= 0 ? FormatSize(size) : "—");
            sizeLabel.style.width = SizeColumnWidth;
            sizeLabel.style.flexShrink = 0;
            sizeLabel.style.opacity = 0.8f;
            row.Add(sizeLabel);

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

            var loadBtn = new Button(() => OnLoadClicked(name)) { text = "Load" };
            loadBtn.SetEnabled(_selectedWorld != null && !_selectedWorld.IsDisposed);
            loadBtn.tooltip =
                "Load this snapshot into the selected world. "
                + "If a recording is active, its in-memory buffer is "
                + "discarded and a fresh recording starts from the "
                + "snapshot's frame.";
            actions.Add(loadBtn);

            var renameBtn = new Button(() => OnRenameClicked(name)) { text = "Rename" };
            renameBtn.style.marginLeft = 2;
            actions.Add(renameBtn);

            var deleteBtn = new Button(() => OnDeleteClicked(name)) { text = "Delete" };
            deleteBtn.style.marginLeft = 2;
            actions.Add(deleteBtn);

            row.Add(actions);
            return row;
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024):0.#} MB";
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
