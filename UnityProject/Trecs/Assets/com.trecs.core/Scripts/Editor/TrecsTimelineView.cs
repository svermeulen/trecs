using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Internal
{
    /// <summary>
    /// The player window's timeline strip: an int slider scrubber with a
    /// hover-cursor indicator + floating frame readout, an adaptive time
    /// ruler underneath, and a marker overlay for keyframes / bookmarks.
    /// Extracted from <see cref="TrecsPlayerWindow"/> so the scrub state
    /// machine (drag detection, throttled commits, final commit on release)
    /// lives next to the only fields it touches.
    ///
    /// The view is presentation + interaction only. What a frame jump or a
    /// bookmark removal *does* stays with the owner via the constructor
    /// callbacks; the owner drives <see cref="Refresh"/> from its periodic
    /// tick and <see cref="Reset"/> when there's nothing to scrub.
    ///
    /// While dragging, value changes commit a jump at most every
    /// <see cref="ScrubThrottleSeconds"/> so the world updates continuously
    /// without per-frame resim cost; pointer release always commits the
    /// released frame exactly.
    /// </summary>
    internal sealed class TrecsTimelineView
    {
        const double ScrubThrottleSeconds = 0.15;

        readonly VisualElement _tipRoot;
        readonly Action<int> _jumpToFrame;
        readonly Action<int, string> _removeBookmark;

        // Live world-frame for the hover readout's signed offset and for
        // seeding the scrub state on pointer-down; null when no world is
        // selected. Read at interaction time (not cached at Refresh) so the
        // offset stays correct while the simulation advances under a
        // stationary cursor.
        readonly Func<int?> _currentFrame;

        // Fixed delta time, or <= 0 when unknown (play hasn't started). Used
        // by the ruler and the hover readout's frames→seconds conversion.
        readonly Func<float> _fixedDeltaTime;

        SliderInt _slider;
        VisualElement _hoverIndicator;
        VisualElement _markerLayer;
        VisualElement _ruler;

        // Floating hover readout pinned above the vertical hover indicator
        // line. Shows the cursor's frame and a signed time offset relative
        // to the current frame ("412 (+12s)") so users can pick a scrub
        // target. Positioned on the indicator (not the cursor) so it stays
        // in line with the visual marker as the user moves.
        TrecsFloatingTip _hoverTip;

        // Scrub-drag state: while pointer is down on the slider, value changes
        // commit a jump at most every ScrubThrottleSeconds, with a final
        // commit on PointerUp guaranteeing we land at the released frame.
        bool _isScrubbing;
        int _pendingScrubFrame;
        int _lastCommittedScrubFrame;
        double _lastScrubCommitTime;

        // Guards the value-changed callback while Refresh/Reset write slider
        // ranges and values programmatically.
        bool _suppressValueEvents;

        public TrecsTimelineView(
            VisualElement tipRoot,
            Action<int> jumpToFrame,
            Action<int, string> removeBookmark,
            Func<int?> currentFrame,
            Func<float> fixedDeltaTime
        )
        {
            _tipRoot = tipRoot ?? throw new ArgumentNullException(nameof(tipRoot));
            _jumpToFrame = jumpToFrame ?? throw new ArgumentNullException(nameof(jumpToFrame));
            _removeBookmark =
                removeBookmark ?? throw new ArgumentNullException(nameof(removeBookmark));
            _currentFrame = currentFrame ?? throw new ArgumentNullException(nameof(currentFrame));
            _fixedDeltaTime =
                fixedDeltaTime ?? throw new ArgumentNullException(nameof(fixedDeltaTime));
            // Compact chip (no border / wrapping) — it's a short, live-updating
            // readout, not a prose tooltip.
            _hoverTip = new TrecsFloatingTip(
                tipRoot,
                bordered: false,
                richText: false,
                maxWidth: null,
                verticalPadding: 2
            );
        }

        /// <summary>
        /// Build the slider (with hover indicator + marker overlay) and the
        /// time ruler into <paramref name="panel"/>, in that order.
        /// </summary>
        public void BuildInto(VisualElement panel)
        {
            _slider = new SliderInt(0, 1) { value = 0, showInputField = true };
            _slider.tooltip =
                "Drag to scrub through the buffer. Click anywhere on the track "
                + "to jump there. Type a frame in the input field on the right "
                + "to jump exactly. Faint white ticks mark keyframes (auto-saved "
                + "recovery points); brighter yellow pins mark snapshots — "
                + "click a marker to jump there, right-click a snapshot to "
                + "remove it.";
            _slider.style.marginTop = 6;
            _slider.RegisterValueChangedCallback(OnValueChanged);
            _slider.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            _slider.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            _slider.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
            _slider.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _slider.RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
            panel.Add(_slider);

            // Thin vertical hover-cursor line layered on top of the slider so
            // there's an unmistakable visual cue of where a click would land.
            // Positioned in OnPointerMove and hidden when the cursor leaves
            // the track or scrubbing starts.
            _hoverIndicator = new VisualElement();
            _hoverIndicator.style.position = Position.Absolute;
            _hoverIndicator.style.width = 1;
            _hoverIndicator.style.top = 0;
            _hoverIndicator.style.bottom = 0;
            _hoverIndicator.style.backgroundColor = new Color(1f, 1f, 1f, 0.55f);
            _hoverIndicator.pickingMode = PickingMode.Ignore;
            _hoverIndicator.style.display = DisplayStyle.None;
            _slider.Add(_hoverIndicator);

            // Keyframe + snapshot markers, layered on top of the slider track.
            // The layer's pickingMode is Ignore so it doesn't intercept
            // scrub interactions in empty space — only the individual
            // markers (added in RefreshMarkerLayer) are pickable. Width
            // gets set to the track-bounds range each refresh so percent-
            // based marker positions stay aligned with the thumb travel
            // (BaseSlider's value↔pixel mapping insets by half the thumb
            // width on each side; matches TryGetSliderTrackBounds).
            _markerLayer = new VisualElement();
            _markerLayer.style.position = Position.Absolute;
            _markerLayer.style.top = 0;
            _markerLayer.style.bottom = 0;
            _markerLayer.pickingMode = PickingMode.Ignore;
            _slider.Add(_markerLayer);

            // Adaptive time ruler directly under the slider — gives an
            // at-a-glance sense of how long the buffer is. Tick interval
            // auto-picks (1s, 5s, 30s, 1m, …) so labels stay readable as
            // the buffer grows.
            _ruler = new VisualElement();
            _ruler.style.height = 12;
            _ruler.style.marginTop = 0;
            _ruler.style.marginBottom = 4;
            _ruler.style.position = Position.Relative;
            _ruler.pickingMode = PickingMode.Ignore;
            panel.Add(_ruler);
        }

        /// <summary>
        /// Snap back to the disabled "empty" state: hide hover chrome, clear
        /// ruler + markers, and reset the slider's range/value to match its
        /// constructed state (so a disabled control doesn't display stale
        /// frame numbers from the last active recording).
        /// </summary>
        public void Reset()
        {
            _hoverIndicator.style.display = DisplayStyle.None;
            _hoverTip.Hide();
            _ruler.Clear();
            _markerLayer?.Clear();
            _suppressValueEvents = true;
            try
            {
                _slider.lowValue = 0;
                _slider.highValue = 1;
                _slider.SetValueWithoutNotify(0);
            }
            finally
            {
                _suppressValueEvents = false;
            }
            _slider.SetEnabled(false);
        }

        /// <summary>
        /// Sync the slider range/value to the buffer and rebuild the ruler +
        /// marker overlay. The slider's value follows <paramref name="currentFrame"/>
        /// unless the user is mid-scrub.
        /// </summary>
        public void Refresh(
            int startFrame,
            int maxFrame,
            int currentFrame,
            IReadOnlyList<WorldSnapshot> keyframes,
            IReadOnlyList<WorldSnapshot> bookmarks
        )
        {
            _slider.SetEnabled(true);

            _suppressValueEvents = true;
            try
            {
                _slider.lowValue = startFrame;
                _slider.highValue = maxFrame;
                if (!_isScrubbing)
                {
                    _slider.SetValueWithoutNotify(currentFrame);
                }
            }
            finally
            {
                _suppressValueEvents = false;
            }

            RefreshRuler(startFrame, maxFrame);
            RefreshMarkerLayer(startFrame, maxFrame, keyframes, bookmarks);
        }

        // ---- Ruler ----

        // Adaptive ruler under the slider: tick labels at "nice" time
        // intervals (1s, 5s, 30s, 1m, …). Picks the smallest interval that
        // keeps labels at least ~70 px apart so as the buffer grows the
        // labels never overlap. Labels are positioned in percent so they
        // reflow with track resizes.
        void RefreshRuler(int startFrame, int maxFrame)
        {
            _ruler.Clear();
            var dt = _fixedDeltaTime();
            if (dt <= 0f)
            {
                return;
            }
            if (!TryGetSliderTrackBounds(out var trackLeft, out var trackWidth) || trackWidth <= 0)
            {
                return;
            }
            // TryGetSliderTrackBounds already returns the thumb-centre
            // range (DC inset by thumb half-width on each end), so the
            // ruler aligns with the thumb's travel range without any
            // further adjustment.
            _ruler.style.marginLeft = trackLeft;
            _ruler.style.width = trackWidth;

            var spanFrames = maxFrame - startFrame;
            if (spanFrames <= 0)
            {
                return;
            }
            var spanSeconds = spanFrames * dt;
            var interval = ChooseRulerInterval(spanSeconds, trackWidth);

            // Walk from 0 to spanSeconds in interval-sized steps. The +ε
            // guards against floating-point miss at the end of the span.
            for (var t = 0f; t <= spanSeconds + 0.0001f; t += interval)
            {
                var fraction = Mathf.Clamp01(t / spanSeconds);

                var tickLine = new VisualElement();
                tickLine.style.position = Position.Absolute;
                tickLine.style.left = new Length(fraction * 100f, LengthUnit.Percent);
                tickLine.style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
                tickLine.style.top = 0;
                tickLine.style.width = 1;
                tickLine.style.height = 3;
                tickLine.style.backgroundColor = new Color(1f, 1f, 1f, 0.4f);
                tickLine.pickingMode = PickingMode.Ignore;
                _ruler.Add(tickLine);

                var label = new Label(FormatRulerLabel(t));
                label.style.position = Position.Absolute;
                label.style.left = new Length(fraction * 100f, LengthUnit.Percent);
                label.style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
                label.style.top = 3;
                label.style.fontSize = 9;
                label.style.color = new Color(1f, 1f, 1f, 0.55f);
                label.pickingMode = PickingMode.Ignore;
                _ruler.Add(label);
            }
        }

        static float ChooseRulerInterval(float spanSeconds, float trackWidthPx)
        {
            // Aim for ~70 px between labels. Step through the candidates
            // and return the smallest interval whose tick count fits.
            // 1s is the floor — sub-second precision was visual noise.
            var maxTicks = Mathf.Max(2f, trackWidthPx / 70f);
            float[] candidates = { 1f, 2f, 5f, 10f, 15f, 30f, 60f, 120f, 300f, 600f };
            foreach (var c in candidates)
            {
                if (spanSeconds / c <= maxTicks)
                {
                    return c;
                }
            }
            return 1200f;
        }

        static string FormatRulerLabel(float seconds)
        {
            if (seconds < 0.5f)
            {
                return "0s";
            }
            if (seconds < 10f)
            {
                var rounded = Mathf.Round(seconds * 10f) / 10f;
                return Mathf.Approximately(rounded, Mathf.Round(rounded))
                    ? $"{rounded:F0}s"
                    : $"{rounded:F1}s";
            }
            if (seconds < 60f)
            {
                return $"{Mathf.RoundToInt(seconds)}s";
            }
            var minutes = (int)(seconds / 60f);
            var remSecs = Mathf.RoundToInt(seconds - minutes * 60f);
            return remSecs == 0 ? $"{minutes}m" : $"{minutes}m{remSecs}s";
        }

        // ---- Markers ----

        // Keyframe / snapshot markers layered on top of the slider track.
        // Keyframes render as a faint half-height tick (background colour
        // matches the ruler ticks for visual consistency) — there can be
        // dozens of them so they have to stay subtle. Snapshots render
        // taller, brighter, and with the user's label as a tooltip;
        // they're the user's deliberate "remember this moment" pins so
        // they earn the visual weight. Both are clickable (left-click
        // jumps via the owner's jumpToFrame callback); snapshots also
        // get a right-click → Remove menu wired to the owner's
        // removeBookmark callback.
        void RefreshMarkerLayer(
            int startFrame,
            int maxFrame,
            IReadOnlyList<WorldSnapshot> keyframes,
            IReadOnlyList<WorldSnapshot> bookmarks
        )
        {
            if (_markerLayer == null)
            {
                return;
            }
            _markerLayer.Clear();
            if (maxFrame <= startFrame)
            {
                return;
            }
            // Use the same thumb-centre range the slider's value↔pixel
            // mapping uses, so a marker at frame F sits exactly under the
            // thumb when the slider is at value F. Falls back to the
            // marker layer's own width if the track bounds aren't
            // resolved yet — degrades gracefully on the first layout
            // pass before the slider has measured itself.
            if (TryGetSliderTrackBounds(out var trackLeft, out var trackWidth) && trackWidth > 0)
            {
                _markerLayer.style.left = trackLeft;
                _markerLayer.style.width = trackWidth;
            }
            var span = (float)(maxFrame - startFrame);
            // Keyframes first (rendered behind snapshots via DOM order, so
            // a snapshot at the same frame visually wins). The first
            // keyframe (StartFrame) is always present and lines up with the
            // slider's lowValue end; we still draw it so users can tell
            // "yes there's a recovery point at the start".
            for (var i = 0; i < keyframes.Count; i++)
            {
                var keyframe = keyframes[i];
                if (keyframe.FixedFrame < startFrame || keyframe.FixedFrame > maxFrame)
                {
                    continue;
                }
                var fraction = Mathf.Clamp01((keyframe.FixedFrame - startFrame) / span);
                _markerLayer.Add(BuildKeyframeMarker(keyframe.FixedFrame, fraction));
            }
            for (var i = 0; i < bookmarks.Count; i++)
            {
                var bookmark = bookmarks[i];
                if (bookmark.FixedFrame < startFrame || bookmark.FixedFrame > maxFrame)
                {
                    continue;
                }
                var fraction = Mathf.Clamp01((bookmark.FixedFrame - startFrame) / span);
                _markerLayer.Add(BuildBookmarkMarker(bookmark, fraction));
            }
        }

        VisualElement BuildKeyframeMarker(int frame, float fraction)
        {
            // Subtle half-height tick that doesn't compete with the
            // snapshot pins or the slider thumb. Width is 2px to give
            // pointer interaction a usable hit-target without becoming
            // visually heavy. Click jumps; right-click is intentionally
            // not wired — keyframes are managed by the recorder's cadence
            // and capacity rules, not the user.
            var marker = new VisualElement();
            marker.style.position = Position.Absolute;
            marker.style.left = new Length(fraction * 100f, LengthUnit.Percent);
            marker.style.top = new Length(50f, LengthUnit.Percent);
            // Centre the half-height tick on both axes — translate -50%
            // horizontally so the marker visually sits ON the keyframe's
            // frame rather than starting from it, and -50% vertically so
            // it straddles the slider track instead of dropping below it.
            marker.style.translate = new Translate(
                new Length(-50, LengthUnit.Percent),
                new Length(-50, LengthUnit.Percent)
            );
            marker.style.width = 2;
            marker.style.height = 8;
            marker.style.backgroundColor = new Color(1f, 1f, 1f, 0.45f);
            marker.tooltip = $"Keyframe @ frame {frame}";
            marker.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }
                _jumpToFrame(frame);
                // Stop propagation so the click doesn't also start a
                // scrub on the underlying slider track.
                evt.StopPropagation();
            });
            return marker;
        }

        VisualElement BuildBookmarkMarker(WorldSnapshot bookmark, float fraction)
        {
            // Taller, brighter pin — yellow to match the "Favorite" star
            // icon used on the capture button. The flag-shaped layout
            // (vertical line + small label-tag at the top) reads as a
            // pin even at the smallest slider widths. Label text comes
            // from the user-supplied bookmark label, falling back to
            // "(unlabelled)" so right-click → Remove still has something
            // to identify in the menu.
            var pinColor = new Color(1f, 0.85f, 0.2f, 0.95f);
            var marker = new VisualElement();
            marker.style.position = Position.Absolute;
            marker.style.left = new Length(fraction * 100f, LengthUnit.Percent);
            marker.style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
            marker.style.top = 0;
            marker.style.bottom = 0;
            marker.style.width = 3;
            marker.style.backgroundColor = pinColor;
            // Capacity for hover-tooltip text; falls back to "(unlabelled)"
            // when the bookmark was captured without a label.
            var labelText = string.IsNullOrEmpty(bookmark.Label) ? "(unlabelled)" : bookmark.Label;
            marker.tooltip = $"Bookmark: {labelText} @ frame {bookmark.FixedFrame}";
            marker.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 1)
                {
                    // Right-click: confirm-and-remove via a tiny context
                    // menu. Single item rather than a generic
                    // ContextualMenuPopulateEvent dispatch so the action
                    // is unambiguous from the user's POV.
                    var menu = new GenericMenu();
                    menu.AddItem(
                        new GUIContent(
                            $"Remove bookmark '{labelText}' @ frame {bookmark.FixedFrame}"
                        ),
                        false,
                        () => _removeBookmark(bookmark.FixedFrame, labelText)
                    );
                    menu.ShowAsContext();
                    evt.StopPropagation();
                    return;
                }
                if (evt.button != 0)
                {
                    return;
                }
                _jumpToFrame(bookmark.FixedFrame);
                evt.StopPropagation();
            });

            // Optional flag-tag at the top so a row of bookmarks reads
            // as distinct pins even when they cluster. Just a small
            // square overlay; we don't render the full label here (would
            // collide with neighbours and the time ruler) — the tooltip
            // carries the full text on hover.
            var flag = new VisualElement();
            flag.style.position = Position.Absolute;
            flag.style.left = -2;
            flag.style.top = -1;
            flag.style.width = 7;
            flag.style.height = 5;
            flag.style.backgroundColor = pinColor;
            flag.pickingMode = PickingMode.Ignore;
            marker.Add(flag);

            return marker;
        }

        // ---- Scrub / hover interaction ----

        void OnPointerDown(PointerDownEvent evt)
        {
            // showInputField=true puts an IntegerField next to the slider track.
            // Clicking inside that field would otherwise enter scrub mode and
            // throttle-jump on every keystroke as the user types — bail out.
            if (evt.target is VisualElement target && IsInsideIntegerField(target))
            {
                return;
            }
            _isScrubbing = true;
            _pendingScrubFrame = _slider.value;
            _lastCommittedScrubFrame = _currentFrame() ?? _pendingScrubFrame;
            _lastScrubCommitTime = EditorApplication.timeSinceStartup;
            _hoverIndicator.style.display = DisplayStyle.None;
        }

        static bool IsInsideIntegerField(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
            {
                if (e is IntegerField)
                {
                    return true;
                }
            }
            return false;
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (_isScrubbing || !_slider.enabledInHierarchy)
            {
                return;
            }
            // Use the slider's internal tracker rect (not the whole slider
            // including label/input field) so the hover frame corresponds to
            // the same x as the thumb would jump to on click.
            if (!TryGetSliderTrackBounds(out var trackLeft, out var trackWidth))
            {
                return;
            }
            var localX = evt.localPosition.x;
            var insideTrack = localX >= trackLeft && localX <= trackLeft + trackWidth;
            if (insideTrack)
            {
                var fraction = (localX - trackLeft) / trackWidth;
                var frame = Mathf.RoundToInt(
                    Mathf.Lerp(_slider.lowValue, _slider.highValue, fraction)
                );
                _hoverIndicator.style.left = Mathf.Round(localX);
                _hoverIndicator.style.display = DisplayStyle.Flex;
                ShowHoverTip(localX, frame);
            }
            else
            {
                _hoverIndicator.style.display = DisplayStyle.None;
                _hoverTip.Hide();
            }
        }

        void OnPointerLeave(PointerLeaveEvent evt)
        {
            _hoverIndicator.style.display = DisplayStyle.None;
            _hoverTip.Hide();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            FinalizeScrub();
        }

        void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            // Slider releases capture when drag ends. If somehow the up event
            // didn't land (e.g. window lost focus), still commit on release.
            FinalizeScrub();
        }

        void FinalizeScrub()
        {
            if (!_isScrubbing)
            {
                return;
            }
            _isScrubbing = false;
            // Always commit the released frame even if a throttle just landed —
            // we want to be exactly where the user released.
            var target = _pendingScrubFrame;
            if (target != _lastCommittedScrubFrame)
            {
                _jumpToFrame(target);
                _lastCommittedScrubFrame = target;
            }
        }

        void OnValueChanged(ChangeEvent<int> evt)
        {
            if (_suppressValueEvents)
            {
                return;
            }
            _pendingScrubFrame = evt.newValue;
            if (_isScrubbing)
            {
                MaybeCommitThrottledScrub();
            }
            else
            {
                // Slider changed via keyboard / input field — commit immediately.
                _jumpToFrame(evt.newValue);
                _lastCommittedScrubFrame = evt.newValue;
            }
        }

        void MaybeCommitThrottledScrub()
        {
            if (_pendingScrubFrame == _lastCommittedScrubFrame)
            {
                return;
            }
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastScrubCommitTime < ScrubThrottleSeconds)
            {
                return;
            }
            _jumpToFrame(_pendingScrubFrame);
            _lastCommittedScrubFrame = _pendingScrubFrame;
            _lastScrubCommitTime = now;
        }

        void ShowHoverTip(float trackLocalX, int hoverFrame)
        {
            var current = _currentFrame();
            if (_isScrubbing || current == null)
            {
                _hoverTip.Hide();
                return;
            }
            _hoverTip.Show(
                $"{hoverFrame} ({FormatRelativeFrameDelta(hoverFrame - current.Value)})",
                size =>
                {
                    // Indicator's root-local x = slider's root-local x +
                    // slider-local x of the indicator (trackLocalX). Centre
                    // the tip horizontally on it; clamp inside the window so
                    // it doesn't slide off near either end of the slider.
                    // Sits just above the slider's top edge.
                    var rootBound = _tipRoot.worldBound;
                    var sliderBound = _slider.worldBound;
                    var indicatorRootX = sliderBound.x - rootBound.x + trackLocalX;
                    var x = Mathf.Clamp(
                        indicatorRootX - size.x / 2f,
                        4f,
                        Mathf.Max(4f, rootBound.width - size.x - 4f)
                    );
                    var y = sliderBound.y - rootBound.y - size.y - 4f;
                    return new Vector2(x, y);
                }
            );
        }

        // Signed, human-readable description of a hover offset from the
        // current frame — e.g. "+12s", "-1m30s". Sub-second offsets render
        // as "0s" (seconds is the smallest unit). Falls back to a raw frame
        // delta if the runner has no fixed-delta-time yet (i.e. play hasn't
        // started).
        string FormatRelativeFrameDelta(int deltaFrames)
        {
            if (deltaFrames == 0)
            {
                return "0s";
            }
            var dt = _fixedDeltaTime();
            if (dt <= 0f)
            {
                return deltaFrames > 0 ? $"+{deltaFrames} frames" : $"{deltaFrames} frames";
            }
            var deltaSeconds = deltaFrames * dt;
            var sign = deltaSeconds > 0 ? "+" : "-";
            return sign + FormatRulerLabel(Mathf.Abs(deltaSeconds));
        }

        bool TryGetSliderTrackBounds(out float left, out float width)
        {
            // Unity's BaseSlider maps cursor X to value over the THUMB-CENTRE
            // range — that's the drag-container inset by the thumb's half
            // width on each side. (Confirmed empirically: clicking with the
            // full drag-container as bounds makes the click land a few
            // frames before the hover-displayed frame, with the gap growing
            // as the cursor moves away from centre.) Match that exactly so
            // hover, click, and the time-ruler all use the same coordinate
            // space.
            var dragContainer = _slider.Q(className: "unity-base-slider__drag-container");
            var dragger = _slider.Q(className: "unity-base-slider__dragger");
            if (
                dragContainer != null
                && dragContainer.layout.width > 0
                && dragger != null
                && dragger.layout.width > 0
            )
            {
                var dcWorld = dragContainer.worldBound;
                var sliderWorld = _slider.worldBound;
                var thumbHalfWidth = dragger.layout.width / 2f;
                left = dcWorld.x - sliderWorld.x + thumbHalfWidth;
                width = Mathf.Max(0f, dcWorld.width - dragger.layout.width);
                return true;
            }
            // Fallback: drag-container without thumb adjustment if the
            // dragger isn't laid out yet.
            if (dragContainer != null && dragContainer.layout.width > 0)
            {
                var dcWorld = dragContainer.worldBound;
                var sliderWorld = _slider.worldBound;
                left = dcWorld.x - sliderWorld.x;
                width = dcWorld.width;
                return true;
            }
            // Tracker fallback for early-layout cases.
            var tracker = _slider.Q(className: "unity-base-slider__tracker");
            if (tracker != null && tracker.layout.width > 0)
            {
                var trackerWorld = tracker.worldBound;
                var sliderWorld = _slider.worldBound;
                left = trackerWorld.x - sliderWorld.x;
                width = trackerWorld.width;
                return true;
            }
            // Final fallback: whole-slider span if nothing's laid out yet.
            var w = _slider.resolvedStyle.width;
            if (w > 0)
            {
                left = 0;
                width = w;
                return true;
            }
            left = 0;
            width = 0;
            return false;
        }
    }
}
