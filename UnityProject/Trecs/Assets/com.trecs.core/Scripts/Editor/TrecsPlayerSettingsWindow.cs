using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Internal
{
    /// <summary>
    /// Modal popup for tuning the Trecs Player's settings (keyframe cadence,
    /// scrub-cache cadence, capacity caps). Backed by
    /// <see cref="TrecsPlayerSettingsStore"/> EditorPrefs so values persist
    /// across editor sessions and apply to all live recorders on Save.
    /// </summary>
    internal class TrecsPlayerSettingsWindow : EditorWindow
    {
        Toggle _autoRecordOnStartField;
        FloatField _keyframeIntervalField;
        FloatField _scrubCacheIntervalField;
        IntegerField _maxKeyframeCountField;
        IntegerField _maxScrubCacheMbField;

        public static void Show(EditorWindow centerOver)
        {
            var window = CreateInstance<TrecsPlayerSettingsWindow>();
            window.titleContent = new GUIContent("Trecs Player Settings");
            const float w = 360f;
            const float h = 290f;
            window.minSize = new Vector2(w, h);
            window.maxSize = new Vector2(w * 2, h);
            if (centerOver != null)
            {
                var ap = centerOver.position;
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
                + "open) and also when loading snapshots. When off, you have to press the Record button to "
                + "begin capturing.";
            root.Add(_autoRecordOnStartField);

            _keyframeIntervalField = new FloatField("Keyframe interval (s)")
            {
                value = TrecsPlayerSettingsStore.KeyframeIntervalSeconds,
            };
            _keyframeIntervalField.tooltip =
                "Simulated seconds between persisted-keyframe captures. Keyframes "
                + "survive Save/Load and bound how far back desync recovery "
                + "or cold-scrub can jump. Larger is smaller files; smaller "
                + "is faster recovery.";
            root.Add(_keyframeIntervalField);

            _scrubCacheIntervalField = new FloatField("Scrub-cache interval (s)")
            {
                value = TrecsPlayerSettingsStore.ScrubCacheIntervalSeconds,
            };
            _scrubCacheIntervalField.tooltip =
                "Simulated seconds between transient scrub-cache captures. "
                + "The scrub cache is in-memory only and makes recent-frame "
                + "scrub-back instant. Smaller is snappier scrubbing.";
            root.Add(_scrubCacheIntervalField);

            _maxKeyframeCountField = new IntegerField("Max keyframe count")
            {
                value = TrecsPlayerSettingsStore.MaxKeyframeCount,
            };
            _maxKeyframeCountField.tooltip = "0 = unbounded. Drop-oldest when hit.";
            root.Add(_maxKeyframeCountField);

            // MB rather than bytes so the input is human-friendly. Max value is
            // clamped to int.MaxValue MB which is well past anything realistic.
            _maxScrubCacheMbField = new IntegerField("Max scrub-cache (MB)")
            {
                value = (int)
                    Math.Min(
                        int.MaxValue,
                        TrecsPlayerSettingsStore.MaxScrubCacheBytes / (1024L * 1024L)
                    ),
            };
            _maxScrubCacheMbField.tooltip =
                "0 = unbounded. Stored in MB; the runtime cap is in bytes. "
                + "Drop-oldest when hit.";
            root.Add(_maxScrubCacheMbField);

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
                keyframeIntervalSeconds: _keyframeIntervalField.value,
                scrubCacheIntervalSeconds: _scrubCacheIntervalField.value,
                maxKeyframeCount: _maxKeyframeCountField.value,
                maxScrubCacheBytes: (long)Math.Max(0, _maxScrubCacheMbField.value) * 1024L * 1024L
            );
            // Push onto any currently-running recorders so the change is
            // visible immediately, not only on next play-mode entry.
            TrecsPlayerSettingsStore.ApplyToAllLiveRecorders();
            Close();
        }

        // Refill the visible fields with TrecsRewindBufferSettings's POCO
        // defaults. Pre-Save so the user can still Cancel without
        // committing — matches "factory reset" gestures elsewhere in
        // editor windows.
        void ResetToDefaults()
        {
            var defaults = new TrecsRewindBufferSettings();
            _autoRecordOnStartField.value = true; // matches AutoRecordEnabled's default in TrecsGameStateActivator
            _keyframeIntervalField.value = defaults.KeyframeIntervalSeconds;
            _scrubCacheIntervalField.value = defaults.ScrubCacheIntervalSeconds;
            _maxKeyframeCountField.value = defaults.MaxKeyframeCount;
            _maxScrubCacheMbField.value = (int)
                Math.Min(int.MaxValue, defaults.MaxScrubCacheBytes / (1024L * 1024L));
        }
    }
}
