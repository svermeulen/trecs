using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Internal
{
    /// <summary>
    /// A single floating tooltip-style <see cref="Label"/> layered over a root
    /// element — the shared primitive under <see cref="TrecsManualTooltips"/>
    /// (per-control hover tips) and <see cref="TrecsTimelineView"/>'s
    /// cursor-following frame readout.
    ///
    /// Owns the two fiddly bits both callers used to hand-roll separately:
    /// the consistent dark-chip styling, and the fact that
    /// <c>resolvedStyle</c> width/height aren't valid until a layout pass has
    /// run. <see cref="Show"/> places the label immediately with the
    /// best-known size (0 when unresolved) and re-runs the caller's placement
    /// once layout settles, so clamped / centred positions are correct from
    /// the second frame even on the very first show.
    /// </summary>
    internal sealed class TrecsFloatingTip
    {
        readonly VisualElement _root;
        readonly bool _bordered;
        readonly bool _richText;
        readonly float? _maxWidth;
        readonly int _verticalPadding;

        Label _label;

        /// <param name="root">Element the label is added to; placements are in its local space.</param>
        /// <param name="bordered">Draw a 1px grey border (the wordy per-control tips use it; the compact frame readout doesn't).</param>
        /// <param name="richText">Enable rich text (e.g. &lt;b&gt; section headers in help tooltips).</param>
        /// <param name="maxWidth">Wrap text at this width; null = single line, size to content.</param>
        /// <param name="verticalPadding">Top/bottom padding in px.</param>
        public TrecsFloatingTip(
            VisualElement root,
            bool bordered,
            bool richText,
            float? maxWidth,
            int verticalPadding
        )
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _bordered = bordered;
            _richText = richText;
            _maxWidth = maxWidth;
            _verticalPadding = verticalPadding;
        }

        /// <summary>
        /// Show <paramref name="text"/> at the position returned by
        /// <paramref name="placeRootLocal"/>, which receives the label's
        /// current resolved size (zero before the first layout pass) and
        /// returns root-local (left, top). The placement runs twice: once
        /// immediately and once after layout settles, so size-dependent
        /// clamping/centring self-corrects on first show.
        /// </summary>
        public void Show(string text, Func<Vector2, Vector2> placeRootLocal)
        {
            EnsureLabel();
            _label.text = text;
            _label.style.display = DisplayStyle.Flex;
            _label.BringToFront();
            Place(placeRootLocal);
            _label
                .schedule.Execute(() =>
                {
                    if (_label.style.display == DisplayStyle.None)
                        return;
                    Place(placeRootLocal);
                })
                .ExecuteLater(0);
        }

        public void Hide()
        {
            if (_label != null)
            {
                _label.style.display = DisplayStyle.None;
            }
        }

        void Place(Func<Vector2, Vector2> placeRootLocal)
        {
            var w = _label.resolvedStyle.width;
            var h = _label.resolvedStyle.height;
            var size = new Vector2(float.IsNaN(w) ? 0f : w, float.IsNaN(h) ? 0f : h);
            var pos = placeRootLocal(size);
            _label.style.left = pos.x;
            _label.style.top = pos.y;
        }

        void EnsureLabel()
        {
            if (_label != null)
                return;
            _label = new Label();
            _label.style.position = Position.Absolute;
            _label.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 0.97f);
            _label.style.color = Color.white;
            _label.style.fontSize = 11;
            _label.style.paddingLeft = 6;
            _label.style.paddingRight = 6;
            _label.style.paddingTop = _verticalPadding;
            _label.style.paddingBottom = _verticalPadding;
            _label.style.borderTopLeftRadius = 3;
            _label.style.borderTopRightRadius = 3;
            _label.style.borderBottomLeftRadius = 3;
            _label.style.borderBottomRightRadius = 3;
            if (_bordered)
            {
                _label.style.borderTopWidth = 1;
                _label.style.borderBottomWidth = 1;
                _label.style.borderLeftWidth = 1;
                _label.style.borderRightWidth = 1;
                var border = new Color(0.4f, 0.4f, 0.4f);
                _label.style.borderTopColor = border;
                _label.style.borderBottomColor = border;
                _label.style.borderLeftColor = border;
                _label.style.borderRightColor = border;
            }
            if (_maxWidth.HasValue)
            {
                _label.style.whiteSpace = WhiteSpace.Normal;
                _label.style.maxWidth = _maxWidth.Value;
            }
            _label.enableRichText = _richText;
            _label.pickingMode = PickingMode.Ignore;
            _label.style.display = DisplayStyle.None;
            _root.Add(_label);
        }
    }
}
