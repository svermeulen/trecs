using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Tiny modal text-input dialog. Returns null on cancel, the trimmed text
    /// on OK. Used by the time-travel window for rename prompts where
    /// EditorUtility.DisplayDialog doesn't suffice (no text input).
    /// </summary>
    public class TrecsTextPromptWindow : EditorWindow
    {
        string _result;
        bool _confirmed;
        string _initialValue;
        string _message;

        /// <summary>
        /// Show a modal text-prompt centred over <paramref name="anchor"/>'s
        /// editor window (so multi-monitor setups don't push the dialog off
        /// the main display). Pass null to fall back to whatever Unity picks.
        /// </summary>
        public static string Prompt(
            string title,
            string message,
            string defaultValue,
            EditorWindow anchor = null
        )
        {
            var window = CreateInstance<TrecsTextPromptWindow>();
            window.titleContent = new GUIContent(title);
            window._initialValue = defaultValue ?? string.Empty;
            window._message = message ?? string.Empty;
            window.minSize = new Vector2(320, 100);
            window.maxSize = new Vector2(640, 100);
            const float w = 320f;
            const float h = 100f;
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
            return window._confirmed ? window._result : null;
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            var msg = new Label(_message);
            msg.style.marginBottom = 6;
            root.Add(msg);

            var field = new TextField { value = _initialValue };
            field.style.flexGrow = 1;
            root.Add(field);
            field.Focus();
            field.SelectAll();
            // TrickleDown so we see Return/Escape before the inner TextField's
            // input element consumes them — otherwise Enter just adds a newline
            // intent / does nothing and the user has to click OK with the mouse.
            field.RegisterCallback<KeyDownEvent>(
                evt =>
                {
                    if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    {
                        Confirm(field.value);
                        evt.StopPropagation();
                    }
                    else if (evt.keyCode == KeyCode.Escape)
                    {
                        Cancel();
                        evt.StopPropagation();
                    }
                },
                TrickleDown.TrickleDown
            );

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            buttons.style.marginTop = 6;

            var cancel = new Button(Cancel) { text = "Cancel" };
            cancel.style.marginRight = 4;
            var ok = new Button(() => Confirm(field.value)) { text = "OK" };
            buttons.Add(cancel);
            buttons.Add(ok);

            root.Add(buttons);
        }

        void Confirm(string value)
        {
            _result = value?.Trim() ?? string.Empty;
            _confirmed = true;
            Close();
        }

        void Cancel()
        {
            _confirmed = false;
            Close();
        }
    }
}
