using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Serialization
{
    /// <summary>
    /// Modal popup for tuning the Trecs Player's settings (snapshot interval,
    /// capacity caps, capacity-overflow action). Backed by
    /// <see cref="TrecsPlayerSettingsStore"/> EditorPrefs so values persist
    /// across editor sessions and apply to all live recorders on Save.
    /// </summary>
    public class TrecsPlayerSettingsWindow : EditorWindow
    {
        Toggle _autoRecordOnStartField;
        FloatField _intervalField;
        IntegerField _maxCountField;
        IntegerField _maxMemoryField;
        EnumField _overflowField;

        public static void Show(EditorWindow anchor)
        {
            var window = CreateInstance<TrecsPlayerSettingsWindow>();
            window.titleContent = new GUIContent("Trecs Player Settings");
            const float w = 360f;
            const float h = 260f;
            window.minSize = new Vector2(w, h);
            window.maxSize = new Vector2(w * 2, h);
            if (anchor != null)
            {
                var ap = anchor.position;
                window.position = new Rect(
                    ap.x + (ap.width - w) / 2f,
                    ap.y + (ap.height - h) / 2f,
                    w,
                    h
                );
            }
            window.ShowModal();
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            var hint = new Label(
                "Settings persist across editor sessions. They auto-apply to "
                    + "the recorder when you enter play mode; Save also pushes "
                    + "them to any currently-running recorders."
            );
            hint.style.opacity = 0.7f;
            hint.style.marginBottom = 6;
            hint.style.whiteSpace = WhiteSpace.Normal;
            root.Add(hint);

            _autoRecordOnStartField = new Toggle("Auto-Record")
            {
                value = TrecsGameStateActivator.AutoRecordEnabled,
            };
            _autoRecordOnStartField.tooltip =
                "When on, recording starts automatically as soon as a Trecs "
                + "world appears in play mode (and the Trecs Player window is "
                + "open) and also when loading bookmarks. When off, you have to press the Record button to "
                + "begin capturing.";
            root.Add(_autoRecordOnStartField);

            _intervalField = new FloatField("Snapshot interval (s)")
            {
                value = TrecsPlayerSettingsStore.SnapshotIntervalSeconds,
            };
            _intervalField.tooltip =
                "Simulated seconds between captured snapshots. Smaller is "
                + "faster scrubbing but uses more memory.";
            root.Add(_intervalField);

            _maxCountField = new IntegerField("Max snapshot count")
            {
                value = TrecsPlayerSettingsStore.MaxSnapshotCount,
            };
            _maxCountField.tooltip = "0 = unbounded.";
            root.Add(_maxCountField);

            // MB rather than bytes so the input is human-friendly. Max value is
            // clamped to int.MaxValue MB which is well past anything realistic.
            _maxMemoryField = new IntegerField("Max memory (MB)")
            {
                value = (int)
                    Math.Min(
                        int.MaxValue,
                        TrecsPlayerSettingsStore.MaxSnapshotMemoryBytes / (1024L * 1024L)
                    ),
            };
            _maxMemoryField.tooltip = "0 = unbounded. Stored in MB; the runtime cap is in bytes.";
            root.Add(_maxMemoryField);

            _overflowField = new EnumField(
                "On capacity hit",
                TrecsPlayerSettingsStore.OverflowAction
            );
            _overflowField.tooltip =
                "DropOldest = roll the buffer (oldest snapshots fall off). "
                + "Pause = stop the fixed phase so you can save/fork/reset.";
            root.Add(_overflowField);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.alignItems = Align.Center;
            buttons.style.marginTop = 12;

            // Reset sits on the left so it doesn't sit next to Save where
            // a misclick could undo intentional edits. Doesn't write to
            // EditorPrefs — just refills the UI with hardcoded defaults so
            // the user can still Cancel back out without committing.
            var reset = new Button(ResetToDefaults) { text = "Reset to defaults" };
            buttons.Add(reset);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            buttons.Add(spacer);

            var cancel = new Button(Close) { text = "Cancel" };
            cancel.style.marginRight = 4;
            var ok = new Button(Save) { text = "Save" };
            buttons.Add(cancel);
            buttons.Add(ok);
            root.Add(buttons);

            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    Save();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    Close();
                    evt.StopPropagation();
                }
            });
        }

        void Save()
        {
            // Auto-record-on-start is a session default — it gates whether
            // the activator auto-starts capture when a controller registers.
            // Toggling it here does NOT mass-start/stop currently-running
            // recorders; the user controls those individually with the
            // Record button.
            TrecsGameStateActivator.AutoRecordEnabled = _autoRecordOnStartField.value;

            TrecsPlayerSettingsStore.Save(
                intervalSeconds: _intervalField.value,
                maxCount: _maxCountField.value,
                maxMemoryBytes: (long)Math.Max(0, _maxMemoryField.value) * 1024L * 1024L,
                overflowAction: (CapacityOverflowAction)_overflowField.value
            );
            // Push onto any currently-running recorders so the change is
            // visible immediately, not only on next play-mode entry.
            TrecsPlayerSettingsStore.ApplyToAllLiveRecorders();
            Close();
        }

        // Refill the visible fields with TrecsAutoRecorderSettings's POCO
        // defaults. Pre-Save so the user can still Cancel without
        // committing — matches "factory reset" gestures elsewhere in
        // editor windows.
        void ResetToDefaults()
        {
            var defaults = new TrecsAutoRecorderSettings();
            _autoRecordOnStartField.value = true; // matches AutoRecordEnabled's default in TrecsGameStateActivator
            _intervalField.value = defaults.SnapshotIntervalSeconds;
            _maxCountField.value = defaults.MaxSnapshotCount;
            _maxMemoryField.value = (int)
                Math.Min(int.MaxValue, defaults.MaxSnapshotMemoryBytes / (1024L * 1024L));
            _overflowField.value = defaults.OverflowAction;
        }
    }
}
