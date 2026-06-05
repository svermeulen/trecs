using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Internal
{
    /// <summary>
    /// Manual tooltip dispatch for editor-window controls. UIElements'
    /// <c>.tooltip</c> + <c>TooltipEvent</c> pipeline doesn't fire reliably
    /// for buttons inside the player window (verified empirically: even an
    /// explicit <c>TooltipEvent</c> callback never sees events there — likely
    /// Unity 6 quirks with scroll containers / IMGUI integration). Instead we
    /// listen for PointerEnter/Leave ourselves, run a delay-and-show against
    /// the root's scheduler, and reposition a single floating label.
    ///
    /// Important: we deliberately do NOT set <c>element.tooltip</c> — Unity's
    /// native tooltip path DOES fire for some elements (verified empirically
    /// too), and when both fire we get two overlapping tooltips on hover.
    /// The text lives in an internal per-target map so dynamic updates (e.g.
    /// the play/pause swap) just call <see cref="Apply"/> again.
    /// </summary>
    internal sealed class TrecsManualTooltips
    {
        const long TooltipDelayMs = 450;
        const float TooltipMaxWidth = 320f;

        readonly VisualElement _root;
        readonly TrecsFloatingTip _tip;
        readonly Dictionary<VisualElement, string> _texts = new();
        IVisualElementScheduledItem _showTask;

        public TrecsManualTooltips(VisualElement root)
        {
            _root = root;
            // Bordered + rich text so wordy tooltips (e.g. the state badge's
            // multi-line legend) read as a proper chip and can use <b> headers.
            _tip = new TrecsFloatingTip(
                root,
                bordered: true,
                richText: true,
                maxWidth: TooltipMaxWidth,
                verticalPadding: 3
            );
        }

        /// <summary>
        /// Set (or update) <paramref name="element"/>'s tooltip text. Hover
        /// callbacks are registered once per element; subsequent calls just
        /// swap the text.
        /// </summary>
        public void Apply(VisualElement element, string text)
        {
            _texts[element] = text;
            if (element.userData is string s && s == "manual-tooltip-attached")
            {
                return;
            }
            element.userData = "manual-tooltip-attached";
            element.RegisterCallback<PointerEnterEvent>(_ => ScheduleShow(element));
            element.RegisterCallback<PointerLeaveEvent>(_ => Hide());
            element.RegisterCallback<PointerDownEvent>(_ => Hide());
            element.RegisterCallback<DetachFromPanelEvent>(_ => Hide());
        }

        public void Hide()
        {
            _showTask?.Pause();
            _showTask = null;
            _tip.Hide();
        }

        void ScheduleShow(VisualElement target)
        {
            _showTask?.Pause();
            var captured = target;
            _showTask = _root.schedule.Execute(() => ShowNow(captured)).StartingIn(TooltipDelayMs);
        }

        void ShowNow(VisualElement target)
        {
            if (target?.panel == null)
            {
                return;
            }
            if (!_texts.TryGetValue(target, out var text) || string.IsNullOrEmpty(text))
            {
                return;
            }
            _tip.Show(
                text,
                size =>
                {
                    // Just below the target, clamped so the tooltip doesn't
                    // run off the window's right edge.
                    var rootBound = _root.worldBound;
                    var bound = target.worldBound;
                    var left = bound.x - rootBound.x;
                    var top = bound.y + bound.height + 4 - rootBound.y;
                    var maxLeft = rootBound.width - size.x - 4;
                    if (left > maxLeft && maxLeft > 0)
                    {
                        left = maxLeft;
                    }
                    return new Vector2(left, top);
                }
            );
        }
    }
}
