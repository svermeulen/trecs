using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Serialization
{
    /// <summary>
    /// Modal popup for tuning the auto-recorder's settings (bookmark interval,
    /// capacity caps, overflow action) at runtime. Settings live on the
    /// per-session TrecsAutoRecorderSettings instance, so changes apply
    /// immediately and reset when the play-mode session ends.
    /// </summary>
    public class TrecsRecorderSettingsWindow : EditorWindow
    {
        TrecsAutoRecorder _recorder;
        FloatField _intervalField;
        IntegerField _maxCountField;
        IntegerField _maxMemoryField;
        EnumField _overflowField;

        public static void Show(TrecsAutoRecorder recorder, EditorWindow anchor)
        {
            var window = CreateInstance<TrecsRecorderSettingsWindow>();
            window.titleContent = new GUIContent("Recorder Settings");
            window._recorder = recorder;
            const float w = 360f;
            const float h = 200f;
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

            var hint = new Label("Settings apply immediately and reset when play mode ends.");
            hint.style.opacity = 0.7f;
            hint.style.marginBottom = 6;
            root.Add(hint);

            _intervalField = new FloatField("Bookmark interval (s)")
            {
                value = _recorder.BookmarkIntervalSeconds,
            };
            _intervalField.tooltip =
                "Simulated seconds between bookmark snapshots. Smaller is "
                + "faster scrubbing but uses more memory.";
            root.Add(_intervalField);

            _maxCountField = new IntegerField("Max bookmark count")
            {
                value = _recorder.MaxBookmarkCount,
            };
            _maxCountField.tooltip = "0 = unbounded.";
            root.Add(_maxCountField);

            // MB rather than bytes so the input is human-friendly. Max value is
            // clamped to int.MaxValue MB which is well past anything realistic.
            _maxMemoryField = new IntegerField("Max memory (MB)")
            {
                value = (int)
                    Math.Min(int.MaxValue, _recorder.MaxBookmarkMemoryBytes / (1024L * 1024L)),
            };
            _maxMemoryField.tooltip = "0 = unbounded. Stored in MB; the runtime cap is in bytes.";
            root.Add(_maxMemoryField);

            _overflowField = new EnumField("On capacity hit", _recorder.OverflowAction);
            _overflowField.tooltip =
                "DropOldest = roll the buffer (oldest bookmarks fall off). "
                + "Pause = stop the fixed phase so you can save/fork/reset.";
            root.Add(_overflowField);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            buttons.style.marginTop = 12;

            var cancel = new Button(Close) { text = "Cancel" };
            cancel.style.marginRight = 4;
            var ok = new Button(Apply) { text = "Apply" };
            buttons.Add(cancel);
            buttons.Add(ok);
            root.Add(buttons);

            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    Apply();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    Close();
                    evt.StopPropagation();
                }
            });
        }

        void Apply()
        {
            if (_recorder != null)
            {
                _recorder.BookmarkIntervalSeconds = _intervalField.value;
                _recorder.MaxBookmarkCount = _maxCountField.value;
                _recorder.MaxBookmarkMemoryBytes =
                    (long)Math.Max(0, _maxMemoryField.value) * 1024L * 1024L;
                _recorder.OverflowAction = (CapacityOverflowAction)_overflowField.value;
            }
            Close();
        }
    }
}
