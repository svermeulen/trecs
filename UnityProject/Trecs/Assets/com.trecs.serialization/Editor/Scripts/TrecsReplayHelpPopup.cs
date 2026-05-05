using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Serialization
{
    /// <summary>
    /// Borderless popup window holding the Replay window's "?" help text.
    /// Driven by hover (not click): the parent window opens this on
    /// PointerEnter to "?" and schedules a hide on PointerLeave; this
    /// window cancels the pending hide when the cursor enters its own
    /// area, so users can move the cursor from "?" into the popup to
    /// scroll without the popup snapping shut.
    ///
    /// Lives in its own EditorWindow because UI Toolkit clips the
    /// in-window manual tooltip at the editor window's bounds — a 70-line
    /// help body just doesn't fit there.
    /// </summary>
    public class TrecsReplayHelpPopup : EditorWindow
    {
        const float Width = 480f;
        const float Height = 360f;
        const long HideDelayMs = 250;

        static TrecsReplayHelpPopup _active;

        IVisualElementScheduledItem _pendingHide;
        string _body;

        public static bool IsOpen => _active != null;

        /// <summary>
        /// Show the popup with its bottom edge a little above the given
        /// screen-space mouse position. The popup is centred horizontally
        /// on the cursor; coordinates are passed through verbatim, so a
        /// negative origin (i.e. a secondary monitor to the left of the
        /// primary on macOS) lands on the correct display.
        /// </summary>
        public static void ShowAtMouse(Vector2 mouseScreenPos, string body)
        {
            // If a popup is already open, just keep it and clear any
            // pending hide. Otherwise create one.
            if (_active != null)
            {
                _active.CancelHideInternal();
                return;
            }
            // "Above mouse": bottom edge a few px above the cursor, centred
            // horizontally. Don't clamp to >= 0 — that would force a popup
            // opened from a window on a secondary monitor onto the primary
            // monitor (which is what produced the cross-monitor bug).
            var x = mouseScreenPos.x - Width / 2f;
            var y = mouseScreenPos.y - Height - 8f;
            var window = CreateInstance<TrecsReplayHelpPopup>();
            window._body = body;
            window.position = new Rect(x, y, Width, Height);
            window.ShowPopup();
            _active = window;
        }

        public static void CloseIfOpen()
        {
            if (_active != null)
            {
                _active.Close();
                _active = null;
            }
        }

        /// <summary>
        /// Called by the trigger button's PointerLeave handler so the popup
        /// dismisses itself shortly after the cursor leaves the button —
        /// unless the cursor lands on the popup before the timer fires.
        /// </summary>
        public static void ScheduleHide()
        {
            if (_active != null)
            {
                _active.ScheduleHideInternal();
            }
        }

        public static void CancelHide()
        {
            if (_active != null)
            {
                _active.CancelHideInternal();
            }
        }

        void ScheduleHideInternal()
        {
            CancelHideInternal();
            _pendingHide = rootVisualElement.schedule.Execute(CloseIfOpen).StartingIn(HideDelayMs);
        }

        void CancelHideInternal()
        {
            _pendingHide?.Pause();
            _pendingHide = null;
        }

        void OnDestroy()
        {
            if (_active == this)
            {
                _active = null;
            }
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            // ShowPopup gives no chrome; draw a subtle border + bg.
            root.style.paddingLeft = 12;
            root.style.paddingRight = 12;
            root.style.paddingTop = 10;
            root.style.paddingBottom = 12;
            root.style.backgroundColor = new Color(0.18f, 0.22f, 0.30f, 1f);
            root.style.borderTopWidth = 1;
            root.style.borderBottomWidth = 1;
            root.style.borderLeftWidth = 1;
            root.style.borderRightWidth = 1;
            var border = new Color(0.45f, 0.45f, 0.45f);
            root.style.borderTopColor = border;
            root.style.borderBottomColor = border;
            root.style.borderLeftColor = border;
            root.style.borderRightColor = border;

            var heading = new Label("Trecs Player — Help");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.fontSize = 14;
            heading.style.marginBottom = 6;
            root.Add(heading);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            var body = new Label(_body);
            body.enableRichText = true;
            body.style.whiteSpace = WhiteSpace.Normal;
            body.style.fontSize = 13;
            body.style.color = Color.white;
            body.style.opacity = 0.95f;
            scroll.Add(body);
            root.Add(scroll);

            // Cancel pending dismiss when the cursor enters the popup, so
            // the user can move from "?" into the popup to scroll. Schedule
            // dismiss when the cursor leaves the popup so it doesn't linger.
            root.RegisterCallback<PointerEnterEvent>(_ => CancelHideInternal());
            root.RegisterCallback<PointerLeaveEvent>(_ => ScheduleHideInternal());
        }
    }
}
