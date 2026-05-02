using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Internal;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Serialization
{
    /// <summary>
    /// Editor UI for <see cref="TrecsGameStateController"/>. Layout:
    /// <list type="bullet">
    /// <item>World dropdown (one row per registered <see cref="World"/>).</item>
    /// <item>Recording header row — current name, New / Save / Save As… /
    /// Load… / Delete actions for the in-memory recording.</item>
    /// <item>Transport panel — state badge, ⏮ ◀ ⏯ ▶ ⏭ buttons, Fork
    /// (visible only in Playback), inline Speed slider. Slider with adaptive
    /// time ruler underneath. Capacity banner when the recorder is paused
    /// against its memory cap.</item>
    /// </list>
    /// The slider commits a JumpToFrame on pointer release; while dragging
    /// the throttled commit fires at most every <c>ScrubThrottleSeconds</c>
    /// so the world updates continuously without per-frame resim cost.
    /// Keyboard shortcuts (Space, Home/End, ←/→, Shift+arrows) drive the
    /// same actions when the window has focus.
    /// </summary>
    public class TrecsTimeTravelWindow : EditorWindow
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsTimeTravelWindow");

        // Speed presets the inline button cycles through. A discrete set
        // matches user expectations (YouTube / VLC / Replay Mod) and is more
        // useful for debug than fine-grained slider control — nobody needs
        // 0.83×.
        static readonly float[] SpeedPresets = { 0.1f, 0.25f, 0.5f, 1f, 2f };

        const string UnsavedRecordingDisplayName = "(unsaved)";

        const string DefaultBadgeTooltip =
            "Recorder state.\n"
            + "LIVE — no recording session active.\n"
            + "REC — recording at the live edge of the buffer.\n"
            + "PLAY — scrubbed back into the buffer (auto-recorder) or "
            + "playing through a loaded recording. ⏸ suffix means paused.";

        DropdownField _worldDropdown;
        Label _emptyState;
        Button _helpButton;
        Button _settingsButton;

        // Recording header row: current-recording name + persistence actions.
        VisualElement _recordingHeaderRow;
        Label _recordingNameLabel;
        Button _newButton;
        Button _saveButton;
        Button _saveAsButton;
        Button _loadButton;
        Button _deleteButton;
        Label _recordingStatusLabel;
        IVisualElementScheduledItem _recordingStatusClearTask;
        const long RecordingStatusClearDelayMs = 4000;

        // Name of the on-disk file backing the in-memory buffer, or null when
        // the buffer has not been saved (or has been New'd / world-switched).
        // Drives the header label, "Save" overwrite-vs-prompt branching, and
        // whether Delete is enabled.
        string _currentRecordingName;

        // Transport panel: state badge, 5 transport buttons, fork button.
        VisualElement _transportPanel;
        Label _stateBadge;
        Button _jumpStartButton;
        Button _stepBackButton;
        Button _playPauseButton;
        Image _playPauseIcon;
        Button _stepForwardButton;
        Button _jumpEndButton;
        Button _forkButton;
        Button _trimButton;

        // Cached editor icons for the transport buttons. Loaded lazily once
        // per domain reload via EditorGUIUtility.IconContent so we get the
        // correct light/dark variants automatically.
        static Texture2D _iconJumpStart;
        static Texture2D _iconStepBack;
        static Texture2D _iconPlay;
        static Texture2D _iconPause;
        static Texture2D _iconStepForward;
        static Texture2D _iconJumpEnd;

        // Scrubber: slider with hover-cursor indicator + adaptive time ruler.
        SliderInt _timelineSlider;
        VisualElement _hoverIndicator;

        // Floating label shown next to the cursor while hovering the slider —
        // displays the cursor's frame number and signed time offset from the
        // current frame. Cleaner than a static text label below the timeline.
        Label _hoverTooltipLabel;

        // Subtle bottom-right stats line: bookmark count, frame span, byte
        // size. Tucked away so it doesn't dominate the UI but available for
        // a glance at the buffer's footprint.
        Label _bufferInfoLabel;
        Label _capacityBanner;

        // Surfaces a desync detected during Playback (simulation re-ran from
        // an earlier bookmark and produced a different state at a frame where
        // we had captured one). Sticks until the buffer "moves" (Reset, Fork,
        // JumpToFrame, load) so the user has time to notice and inspect.
        Label _desyncBanner;
        VisualElement _timelineRuler;

        // Scrub-drag state: while pointer is down on the slider, value changes
        // commit a JumpToFrame at most every ScrubThrottleSeconds, with a
        // final commit on PointerUp guaranteeing we land at the released frame.
        bool _isScrubbing;
        int _pendingScrubFrame;
        int _lastCommittedScrubFrame;
        double _lastScrubCommitTime;
        const double ScrubThrottleSeconds = 0.15;

        // Hover state: while pointer is over the slider but not pressed, this
        // is the frame the cursor is hovering over (read from local pointer
        // position). Cleared on PointerLeave.
        int? _hoverFrame;

        // Manual tooltip system. UIElements' built-in TooltipEvent dispatch
        // is unreliable for buttons in this window (likely Unity 6 quirks
        // with scroll containers / IMGUI integration), so we drive a small
        // floating Label ourselves on PointerEnter/Leave. Fields live at the
        // window level so the same label is reused for every tooltip target.
        Label _tooltipLabel;
        IVisualElementScheduledItem _tooltipShowTask;
        const long TooltipDelayMs = 450;
        const float DefaultTooltipMaxWidth = 320f;

        // Per-target max-width override so the help button's "?" can show
        // a much wider tooltip without affecting transport-button tooltips.
        readonly Dictionary<VisualElement, float> _tooltipMaxWidths = new();

        // Pending open of the help popup window. Cancelled if the cursor
        // leaves "?" before the delay elapses.
        IVisualElementScheduledItem _pendingHelpShow;

        // Speed dropdown: a single button labelled with the current
        // multiplier ("1×") that opens a GenericMenu of preset speeds.
        Button _speedButton;

        World _selectedWorld;
        WorldAccessor _selectedAccessor;
        readonly List<World> _dropdownWorlds = new();

        bool _suppressControlEvents;

        [MenuItem("Window/Trecs/Replay")]
        public static void ShowWindow()
        {
            var window = GetWindow<TrecsTimeTravelWindow>();
            window.titleContent = new GUIContent("Trecs Replay");
            window.minSize = new Vector2(320, 260);
        }

        void OnEnable()
        {
            WorldRegistry.WorldRegistered += OnWorldRegistered;
            WorldRegistry.WorldUnregistered += OnWorldUnregistered;
            TrecsGameStateRegistry.ControllerRegistered += OnControllerRegisteredOrUnregistered;
            TrecsGameStateRegistry.ControllerUnregistered += OnControllerRegisteredOrUnregistered;
            TrecsEditorSelection.ActiveWorldChanged += OnSharedActiveWorldChanged;

            // Auto-start any controllers that registered before this window
            // opened — so the user can press Play first, then open the window
            // without losing access to the buffer (and without missing frames
            // beyond the cap, since recording auto-pauses on capacity).
            TrecsGameStateActivator.StartAllIdleControllers();
        }

        void OnDisable()
        {
            WorldRegistry.WorldRegistered -= OnWorldRegistered;
            WorldRegistry.WorldUnregistered -= OnWorldUnregistered;
            TrecsGameStateRegistry.ControllerRegistered -= OnControllerRegisteredOrUnregistered;
            TrecsGameStateRegistry.ControllerUnregistered -= OnControllerRegisteredOrUnregistered;
            TrecsEditorSelection.ActiveWorldChanged -= OnSharedActiveWorldChanged;
            ClearAccessor();
        }

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

        void OnControllerRegisteredOrUnregistered(TrecsGameStateController _)
        {
            // Header label may need to flip between "(unsaved)" and any
            // backing-file name once a controller appears or disappears.
            UpdateRecordingHeader();
        }

        void CreateGUI()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            // Header/transport rows can grow wider than the window when it's
            // dragged narrow (each button is fixed-width), which would otherwise
            // surface a horizontal scrollbar. Buttons clipping off-screen is
            // preferable to a scrollbar — the user can just widen the window.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            rootVisualElement.Add(scroll);

            var root = scroll.contentContainer;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            _worldDropdown = new DropdownField("World", new List<string>(), 0);
            _worldDropdown.tooltip =
                "Trecs World whose recorder this window drives. One row per "
                + "registered world (typically host + client).";
            _worldDropdown.RegisterValueChangedCallback(OnWorldDropdownChanged);
            root.Add(_worldDropdown);

            _emptyState = new Label("No active worlds (enter Play mode)");
            _emptyState.style.marginTop = 8;
            _emptyState.style.opacity = 0.7f;
            root.Add(_emptyState);

            _recordingHeaderRow = BuildRecordingHeaderRow();
            root.Add(_recordingHeaderRow);

            _transportPanel = BuildTransportPanel();
            root.Add(_transportPanel);

            // Bottom-right stats overlay. Sits on rootVisualElement (above
            // the scroll content) so it stays anchored as the scroll grows
            // and doesn't fight transport-panel layout. Picking is off so
            // it never intercepts clicks on whatever's underneath.
            _bufferInfoLabel = new Label();
            _bufferInfoLabel.style.position = Position.Absolute;
            _bufferInfoLabel.style.bottom = 4;
            _bufferInfoLabel.style.right = 8;
            _bufferInfoLabel.style.fontSize = 10;
            _bufferInfoLabel.style.opacity = 0.45f;
            _bufferInfoLabel.style.color = Color.white;
            _bufferInfoLabel.pickingMode = PickingMode.Ignore;
            _bufferInfoLabel.tooltip =
                "In-memory buffer summary: source mode · bookmark count · "
                + "frame span (and elapsed seconds) · total bytes.";
            rootVisualElement.Add(_bufferInfoLabel);

            RebuildDropdown();
            UpdateRecordingHeader();
            root.schedule.Execute(RefreshTick)
                .Every(TrecsDebugWindowSettings.Get().RefreshIntervalMs);

            rootVisualElement.focusable = true;
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            // Don't steal keys when a TextField has focus (bookmark/recording
            // name typing). The event target is usually the TextField's
            // internal TextElement so walk up for the ancestor check.
            if (evt.target is VisualElement target && IsInsideTextField(target))
            {
                return;
            }
            switch (evt.keyCode)
            {
                case KeyCode.Space:
                    OnPlayPauseClicked();
                    evt.StopPropagation();
                    break;
                case KeyCode.Home:
                    OnJumpStartClicked();
                    evt.StopPropagation();
                    break;
                case KeyCode.End:
                    OnJumpEndClicked();
                    evt.StopPropagation();
                    break;
                case KeyCode.LeftArrow:
                    if (evt.shiftKey)
                    {
                        GetController()?.JumpToPreviousBookmark();
                    }
                    else
                    {
                        OnStepBackClicked();
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.RightArrow:
                    if (evt.shiftKey)
                    {
                        GetController()?.JumpToNextBookmark();
                    }
                    else
                    {
                        OnStepForwardClicked();
                    }
                    evt.StopPropagation();
                    break;
            }
        }

        VisualElement BuildTransportPanel()
        {
            var panel = new VisualElement();
            panel.style.marginTop = 12;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            _stateBadge = new Label("LIVE");
            _stateBadge.tooltip = DefaultBadgeTooltip;
            _stateBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            _stateBadge.style.fontSize = 10;
            _stateBadge.style.minWidth = 56;
            _stateBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            _stateBadge.style.paddingLeft = 6;
            _stateBadge.style.paddingRight = 6;
            _stateBadge.style.paddingTop = 2;
            _stateBadge.style.paddingBottom = 2;
            _stateBadge.style.marginRight = 6;
            _stateBadge.style.borderTopLeftRadius = 3;
            _stateBadge.style.borderTopRightRadius = 3;
            _stateBadge.style.borderBottomLeftRadius = 3;
            _stateBadge.style.borderBottomRightRadius = 3;
            row.Add(_stateBadge);

            EnsureTransportIcons();

            _jumpStartButton = MakeIconTransportButton(_iconJumpStart, "⏮", OnJumpStartClicked);
            ApplyTooltip(_jumpStartButton, "Jump to start of buffer (Home)");
            _stepBackButton = MakeIconTransportButton(_iconStepBack, "◂", OnStepBackClicked);
            ApplyTooltip(
                _stepBackButton,
                "Step back one frame (←)\nShift+← jumps to previous bookmark"
            );
            _playPauseButton = MakeIconTransportButton(_iconPlay, "▶", OnPlayPauseClicked);
            _playPauseIcon = _playPauseButton.Q<Image>();
            ApplyTooltip(_playPauseButton, "Play / Pause (Space)");
            _stepForwardButton = MakeIconTransportButton(
                _iconStepForward,
                "▸",
                OnStepForwardClicked
            );
            ApplyTooltip(
                _stepForwardButton,
                "Step forward one frame (→)\nShift+→ jumps to next bookmark"
            );
            _jumpEndButton = MakeIconTransportButton(_iconJumpEnd, "⏭", OnJumpEndClicked);
            ApplyTooltip(_jumpEndButton, "Go to live edge (End)");
            row.Add(_jumpStartButton);
            row.Add(_stepBackButton);
            row.Add(_playPauseButton);
            row.Add(_stepForwardButton);
            row.Add(_jumpEndButton);

            _forkButton = new Button(OnForkClicked) { text = "Fork" };
            ApplyTooltip(
                _forkButton,
                "Commit the current scrub frame as the new branch point and resume "
                    + "live Recording from here. Auto-recording: trailing buffer is "
                    + "truncated. Loaded recording: saved file is untouched; only the "
                    + "in-memory tail is dropped. (Plain Play just walks forward through "
                    + "the buffer — Fork is the explicit commit.)"
            );
            _forkButton.style.marginLeft = 8;
            _forkButton.style.display = DisplayStyle.None;
            row.Add(_forkButton);

            // Drop-down for "Trim before" / "Trim after" the current frame.
            // Both operations modify the in-memory buffer only; saved files on
            // disk are untouched. Useful for reducing recording size before
            // saving (drop lead-in / tail) without forking & re-recording.
            _trimButton = new Button(OnTrimButtonClicked) { text = "Trim ▾" };
            ApplyTooltip(
                _trimButton,
                "Drop bookmarks from before or after the current frame. "
                    + "Saved files on disk are untouched. Useful for shrinking the "
                    + "in-memory buffer before saving."
            );
            _trimButton.style.marginLeft = 4;
            row.Add(_trimButton);

            // Spacer so the speed button sits on the right edge of the row
            // (matching the layout the speed slider used to provide).
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            // Speed dropdown — discrete presets cover the realistic debug
            // range (frame-step-like 0.1× through 2× fast-forward); finer
            // control isn't actually useful when the goal is "watch this
            // moment slowly" or "skip ahead a bit". Click opens a menu of
            // presets via GenericMenu (matches the Load… menu's style).
            _speedButton = new Button(OnSpeedButtonClicked) { text = FormatSpeedLabel(1f) };
            _speedButton.style.minWidth = 44;
            _speedButton.style.marginLeft = 4;
            ApplyTooltip(
                _speedButton,
                "Simulation speed. Click for presets (0.1×, 0.25×, 0.5×, 1×, 2×). "
                    + "Shown amber when not at real-time."
            );
            row.Add(_speedButton);

            panel.Add(row);

            _timelineSlider = new SliderInt(0, 1) { value = 0, showInputField = true };
            _timelineSlider.tooltip =
                "Drag to scrub through the buffer. Click anywhere on the track "
                + "to jump there. Type a frame in the input field on the right "
                + "to jump exactly. Blue ticks mark stored bookmarks.";
            _timelineSlider.style.marginTop = 6;
            _timelineSlider.RegisterValueChangedCallback(OnTimelineValueChanged);
            _timelineSlider.RegisterCallback<PointerDownEvent>(
                OnTimelinePointerDown,
                TrickleDown.TrickleDown
            );
            _timelineSlider.RegisterCallback<PointerUpEvent>(
                OnTimelinePointerUp,
                TrickleDown.TrickleDown
            );
            _timelineSlider.RegisterCallback<PointerCaptureOutEvent>(OnTimelinePointerCaptureOut);
            _timelineSlider.RegisterCallback<PointerMoveEvent>(OnTimelinePointerMove);
            _timelineSlider.RegisterCallback<PointerLeaveEvent>(OnTimelinePointerLeave);
            panel.Add(_timelineSlider);

            // Thin vertical hover-cursor line layered on top of the slider so
            // there's an unmistakable visual cue of where a click would land.
            // Positioned in OnTimelinePointerMove and hidden when the cursor
            // leaves the track or scrubbing starts.
            _hoverIndicator = new VisualElement();
            _hoverIndicator.style.position = Position.Absolute;
            _hoverIndicator.style.width = 1;
            _hoverIndicator.style.top = 0;
            _hoverIndicator.style.bottom = 0;
            _hoverIndicator.style.backgroundColor = new Color(1f, 1f, 1f, 0.55f);
            _hoverIndicator.pickingMode = PickingMode.Ignore;
            _hoverIndicator.style.display = DisplayStyle.None;
            _timelineSlider.Add(_hoverIndicator);

            // Adaptive time ruler directly under the slider — gives an
            // at-a-glance sense of how long the buffer is. Tick interval
            // auto-picks (0.5s, 1s, 5s, 30s, 1m, …) so labels stay readable
            // as the buffer grows.
            _timelineRuler = new VisualElement();
            _timelineRuler.style.height = 12;
            _timelineRuler.style.marginTop = 0;
            _timelineRuler.style.marginBottom = 4;
            _timelineRuler.style.position = Position.Relative;
            _timelineRuler.pickingMode = PickingMode.Ignore;
            panel.Add(_timelineRuler);

            _capacityBanner = new Label();
            _capacityBanner.tooltip =
                "The recorder hit its configured cap and paused fixed phase to "
                + "prevent runaway memory. Save / Fork / Reset to relieve.";
            _capacityBanner.style.marginTop = 4;
            _capacityBanner.style.paddingLeft = 6;
            _capacityBanner.style.paddingRight = 6;
            _capacityBanner.style.paddingTop = 2;
            _capacityBanner.style.paddingBottom = 2;
            _capacityBanner.style.color = Color.white;
            _capacityBanner.style.backgroundColor = new Color(0.6f, 0.25f, 0.05f);
            _capacityBanner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _capacityBanner.style.whiteSpace = WhiteSpace.Normal;
            _capacityBanner.style.display = DisplayStyle.None;
            panel.Add(_capacityBanner);

            _desyncBanner = new Label();
            _desyncBanner.tooltip =
                "The simulation produced a different world state than originally "
                + "captured at this frame. Indicates non-determinism in your code or "
                + "data — common causes: reading Time.time, using Random outside "
                + "the deterministic RNG, mutating shared static state, or ordering "
                + "issues in component access.";
            _desyncBanner.style.marginTop = 4;
            _desyncBanner.style.paddingLeft = 6;
            _desyncBanner.style.paddingRight = 6;
            _desyncBanner.style.paddingTop = 2;
            _desyncBanner.style.paddingBottom = 2;
            _desyncBanner.style.color = Color.white;
            _desyncBanner.style.backgroundColor = new Color(0.7f, 0.15f, 0.15f);
            _desyncBanner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _desyncBanner.style.whiteSpace = WhiteSpace.Normal;
            _desyncBanner.style.display = DisplayStyle.None;
            panel.Add(_desyncBanner);

            return panel;
        }

        // Builds an icon-styled transport button. Uses Unity's built-in
        // editor icons so we get crisp, monochrome glyphs that match the
        // surrounding editor chrome — much cleaner than Unicode characters
        // (⏮ ◀ ▶ ⏭) which macOS renders as colored emoji-style boxes.
        // Falls back to plain text if the icon couldn't be loaded. Caller
        // registers the tooltip via ApplyTooltip after construction.
        static Button MakeIconTransportButton(Texture2D icon, string fallbackText, Action onClick)
        {
            var b = new Button(onClick);
            b.style.width = 30;
            b.style.height = 22;
            b.style.marginLeft = 1;
            b.style.marginRight = 1;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            if (icon != null)
            {
                var img = new Image
                {
                    image = icon,
                    scaleMode = ScaleMode.ScaleToFit,
                    pickingMode = PickingMode.Ignore,
                };
                img.style.width = 14;
                img.style.height = 14;
                b.Add(img);
            }
            else
            {
                b.text = fallbackText;
            }
            return b;
        }

        // Manual tooltip dispatch. UIElements' .tooltip + TooltipEvent
        // pipeline doesn't fire reliably for our transport buttons (verified
        // empirically: even an explicit TooltipEvent callback never sees
        // events in this window). Instead we listen for PointerEnter/Leave
        // ourselves, run a delay-and-show against rootVisualElement.schedule,
        // and reposition a single floating Label. The .tooltip property is
        // still set so any caller mutating it (e.g. the play/pause text
        // swapping with state) is picked up automatically on next show.
        void ApplyTooltip(
            VisualElement element,
            string text,
            float maxWidth = DefaultTooltipMaxWidth
        )
        {
            element.tooltip = text;
            _tooltipMaxWidths[element] = maxWidth;
            if (element.userData is string s && s == "manual-tooltip-attached")
            {
                return;
            }
            element.userData = "manual-tooltip-attached";
            element.RegisterCallback<PointerEnterEvent>(_ => ScheduleTooltipShow(element));
            element.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());
            element.RegisterCallback<PointerDownEvent>(_ => HideTooltip());
            element.RegisterCallback<DetachFromPanelEvent>(_ => HideTooltip());
        }

        void ScheduleTooltipShow(VisualElement target)
        {
            _tooltipShowTask?.Pause();
            var captured = target;
            _tooltipShowTask = rootVisualElement
                .schedule.Execute(() => ShowTooltipNow(captured))
                .StartingIn(TooltipDelayMs);
        }

        void ShowTooltipNow(VisualElement target)
        {
            var text = target?.tooltip;
            if (string.IsNullOrEmpty(text) || target.panel == null)
            {
                return;
            }
            EnsureTooltipLabel();
            _tooltipLabel.text = text;
            _tooltipLabel.style.maxWidth = _tooltipMaxWidths.TryGetValue(target, out var maxWidth)
                ? maxWidth
                : DefaultTooltipMaxWidth;
            _tooltipLabel.style.display = DisplayStyle.Flex;
            _tooltipLabel.BringToFront();

            // Position just below the target. Convert worldBound into
            // rootVisualElement-local coords. If we'd run off the right
            // edge, clamp so the tooltip stays fully visible.
            var rootBound = rootVisualElement.worldBound;
            var bound = target.worldBound;
            var localLeft = bound.x - rootBound.x;
            var localTop = bound.y + bound.height + 4 - rootBound.y;
            // Force a layout pass so we know the tooltip's own width before
            // clamping. resolvedStyle.width is the laid-out value.
            _tooltipLabel.style.left = localLeft;
            _tooltipLabel.style.top = localTop;
            _tooltipLabel
                .schedule.Execute(() =>
                {
                    if (_tooltipLabel.style.display == DisplayStyle.None)
                        return;
                    var w = _tooltipLabel.resolvedStyle.width;
                    var maxLeft = rootBound.width - w - 4;
                    if (localLeft > maxLeft && maxLeft > 0)
                    {
                        _tooltipLabel.style.left = maxLeft;
                    }
                })
                .ExecuteLater(0);
        }

        void HideTooltip()
        {
            _tooltipShowTask?.Pause();
            _tooltipShowTask = null;
            if (_tooltipLabel != null)
            {
                _tooltipLabel.style.display = DisplayStyle.None;
            }
        }

        void EnsureTooltipLabel()
        {
            if (_tooltipLabel != null)
                return;
            _tooltipLabel = new Label();
            _tooltipLabel.style.position = Position.Absolute;
            _tooltipLabel.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 0.97f);
            _tooltipLabel.style.color = Color.white;
            _tooltipLabel.style.fontSize = 11;
            _tooltipLabel.style.paddingLeft = 6;
            _tooltipLabel.style.paddingRight = 6;
            _tooltipLabel.style.paddingTop = 3;
            _tooltipLabel.style.paddingBottom = 3;
            _tooltipLabel.style.borderTopLeftRadius = 3;
            _tooltipLabel.style.borderTopRightRadius = 3;
            _tooltipLabel.style.borderBottomLeftRadius = 3;
            _tooltipLabel.style.borderBottomRightRadius = 3;
            _tooltipLabel.style.borderTopWidth = 1;
            _tooltipLabel.style.borderBottomWidth = 1;
            _tooltipLabel.style.borderLeftWidth = 1;
            _tooltipLabel.style.borderRightWidth = 1;
            var border = new Color(0.4f, 0.4f, 0.4f);
            _tooltipLabel.style.borderTopColor = border;
            _tooltipLabel.style.borderBottomColor = border;
            _tooltipLabel.style.borderLeftColor = border;
            _tooltipLabel.style.borderRightColor = border;
            _tooltipLabel.style.whiteSpace = WhiteSpace.Normal;
            _tooltipLabel.style.maxWidth = DefaultTooltipMaxWidth;
            _tooltipLabel.pickingMode = PickingMode.Ignore;
            _tooltipLabel.style.display = DisplayStyle.None;
            // Enabled for the help-button "?" tooltip so its <b>section
            // headers</b> render. Harmless for plain-text button tooltips.
            _tooltipLabel.enableRichText = true;
            rootVisualElement.Add(_tooltipLabel);
        }

        static void EnsureTransportIcons()
        {
            // EditorGUIUtility.IconContent returns a GUIContent whose .image
            // is the appropriate light/dark variant for the active skin. The
            // icons are managed by Unity, so we just cache the texture
            // reference; no manual disposal needed.
            _iconJumpStart ??= LoadEditorIcon("Animation.FirstKey");
            _iconStepBack ??= LoadEditorIcon("Animation.PrevKey");
            _iconPlay ??= LoadEditorIcon("PlayButton");
            _iconPause ??= LoadEditorIcon("PauseButton");
            _iconStepForward ??= LoadEditorIcon("Animation.NextKey");
            _iconJumpEnd ??= LoadEditorIcon("Animation.LastKey");
        }

        static Texture2D LoadEditorIcon(string name)
        {
            try
            {
                var content = EditorGUIUtility.IconContent(name);
                return content?.image as Texture2D;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- Help popup (hover-driven) ----
        //
        // The body lives in its own EditorWindow (TrecsReplayHelpPopup)
        // because the manual-tooltip plumbing is bounded by the editor
        // window's rectangle, and 70 lines of help just don't fit. Hover
        // semantics mirror native tooltips: open after the standard delay
        // on PointerEnter, close shortly after PointerLeave — but
        // cancellable while the cursor is over the popup itself so the
        // user can scroll and read.

        void OnHelpHoverEnter(PointerEnterEvent evt)
        {
            // Cursor came back onto "?" — kill any pending dismiss.
            TrecsReplayHelpPopup.CancelHide();
            if (TrecsReplayHelpPopup.IsOpen)
            {
                return;
            }
            // Capture the mouse position now (in screen coords) so the
            // popup opens above where the user actually is, even if their
            // cursor drifts during the show delay. evt.position is panel-
            // local; window.position is the editor window's screen rect.
            // Adding them yields a screen-space mouse position that
            // works on multi-monitor setups (no clamping to ≥ 0).
            var mouseScreenPos = new Vector2(
                position.x + evt.position.x,
                position.y + evt.position.y
            );
            _pendingHelpShow?.Pause();
            _pendingHelpShow = rootVisualElement
                .schedule.Execute(() =>
                    TrecsReplayHelpPopup.ShowAtMouse(mouseScreenPos, HelpBodyText)
                )
                .StartingIn(TooltipDelayMs);
        }

        void OnHelpHoverLeave()
        {
            // Cancel any not-yet-fired open; if the popup is already open,
            // ask it to dismiss itself shortly. The popup's own
            // PointerEnter handler will cancel that schedule if the user
            // moves the cursor onto it.
            _pendingHelpShow?.Pause();
            _pendingHelpShow = null;
            TrecsReplayHelpPopup.ScheduleHide();
        }

        const string HelpBodyText =
            "Record your simulation, scrub back to any earlier moment, and "
            + "replay forward — useful for diagnosing transient bugs.\n\n"
            + "<b>Keyboard shortcuts</b>\n"
            + "  Space          Play / Pause\n"
            + "  Home / End     Jump to buffer start / live edge\n"
            + "  ← / →          Step back / forward one frame\n"
            + "  Shift+← / →    Jump to previous / next bookmark\n\n"
            + "<b>State badge</b>\n"
            + "  LIVE  No recording active\n"
            + "  REC   Recording at the live edge\n"
            + "  PLAY  Scrubbed back, or playing a loaded recording\n"
            + "(The play/pause button turns green when paused.)\n\n"
            + "<b>Recording vs Playback</b>\n"
            + "You're in Playback whenever you've scrubbed back from the "
            + "live edge or loaded a recording from disk. Live input "
            + "systems are silenced; the inputs captured during recording "
            + "drive the world instead, so pressing Play replays the "
            + "session verbatim until the buffer's tail.\n\n"
            + "<b>Fork (Playback only)</b>\n"
            + "Plain Play walks through the buffer without changing it. "
            + "Fork is the explicit \"this timeline, not that one\" "
            + "gesture: it commits the current scrub position as a new "
            + "branch point and resumes live recording from there, "
            + "dropping the trailing buffer.\n\n"
            + "<b>Auto-recording vs loaded recording</b>\n"
            + "  Auto: live in-memory buffer; header shows '(unsaved)' "
            + "until you Save.\n"
            + "  Loaded: a saved recording from disk; trailing bookmarks "
            + "are preserved during scrub so you can replay the same "
            + "moment repeatedly.";

        // Top-level header row showing the in-memory recording's name (or
        // "(unsaved)") with persistence actions. Replaces the older Saved
        // Recordings + Named Bookmarks foldouts; standalone bookmarks are
        // intentionally dropped — the recording is the single unit users
        // think about.
        VisualElement BuildRecordingHeaderRow()
        {
            var panel = new VisualElement();
            panel.style.marginTop = 10;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            _recordingNameLabel = new Label(UnsavedRecordingDisplayName);
            _recordingNameLabel.style.flexGrow = 1;
            // Long names previously pushed the action buttons off-screen and
            // triggered a horizontal scroll bar. Clip with ellipsis instead;
            // the full name is available via the manual tooltip on hover.
            _recordingNameLabel.style.flexShrink = 1;
            _recordingNameLabel.style.minWidth = 0;
            _recordingNameLabel.style.overflow = Overflow.Hidden;
            _recordingNameLabel.style.textOverflow = TextOverflow.Ellipsis;
            _recordingNameLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _recordingNameLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _recordingNameLabel.style.opacity = 0.7f;
            _recordingNameLabel.style.marginRight = 4;
            ApplyTooltip(
                _recordingNameLabel,
                "Name of the on-disk file backing the in-memory recording. "
                    + "'(unsaved)' until you Save."
            );
            row.Add(_recordingNameLabel);

            _newButton = new Button(OnNewClicked) { text = "New" };
            ApplyTooltip(
                _newButton,
                "Discard the current in-memory recording and start fresh "
                    + "from the current frame. Saved files on disk are not affected."
            );
            row.Add(_newButton);

            _saveButton = new Button(OnSaveClicked) { text = "Save" };
            _saveButton.style.marginLeft = 2;
            ApplyTooltip(
                _saveButton,
                "Save the in-memory recording. Prompts for a name on the "
                    + "first save; later saves overwrite the same file."
            );
            row.Add(_saveButton);

            _saveAsButton = new Button(OnSaveAsClicked) { text = "Save As…" };
            _saveAsButton.style.marginLeft = 2;
            ApplyTooltip(_saveAsButton, "Save the in-memory recording under a new name.");
            row.Add(_saveAsButton);

            _loadButton = new Button(OnLoadClicked) { text = "Load…" };
            _loadButton.style.marginLeft = 2;
            ApplyTooltip(_loadButton, "Load a saved recording from disk.");
            row.Add(_loadButton);

            _deleteButton = new Button(OnDeleteClicked) { text = "Delete" };
            _deleteButton.style.marginLeft = 2;
            ApplyTooltip(
                _deleteButton,
                "Delete the current recording's saved file from disk. "
                    + "The in-memory buffer is left untouched."
            );
            row.Add(_deleteButton);

            // Settings cog opens a modal popup for tuning recorder knobs
            // (interval, capacity caps, overflow action) without leaving
            // the timeline window.
            _settingsButton = new Button(OnSettingsClicked) { text = "⚙" };
            _settingsButton.style.minWidth = 22;
            _settingsButton.style.marginLeft = 6;
            ApplyTooltip(
                _settingsButton,
                "Recorder settings: bookmark interval, capacity caps, overflow action."
            );
            row.Add(_settingsButton);

            // Help "?" lives at the far right of the header so it's the
            // last item users scan past before the action buttons end.
            // Hover (with a 450 ms delay matching native tooltip cadence)
            // opens a TrecsReplayHelpPopup in its own window, since UI
            // Toolkit clips a normal in-window tooltip at the editor
            // window's bounds and the help body doesn't fit.
            _helpButton = new Button() { text = "?" };
            _helpButton.style.minWidth = 22;
            _helpButton.style.marginLeft = 4;
            _helpButton.tooltip = "Hover for keyboard shortcuts and help.";
            _helpButton.RegisterCallback<PointerEnterEvent>(OnHelpHoverEnter);
            _helpButton.RegisterCallback<PointerLeaveEvent>(_ => OnHelpHoverLeave());
            _helpButton.RegisterCallback<DetachFromPanelEvent>(_ =>
                TrecsReplayHelpPopup.CloseIfOpen()
            );
            row.Add(_helpButton);

            panel.Add(row);

            _recordingStatusLabel = new Label();
            _recordingStatusLabel.style.marginTop = 2;
            _recordingStatusLabel.style.opacity = 0.7f;
            panel.Add(_recordingStatusLabel);

            return panel;
        }

        void OnWorldRegistered(World world) => RebuildDropdown();

        void OnWorldUnregistered(World world) => RebuildDropdown();

        void RebuildDropdown()
        {
            if (_worldDropdown == null)
            {
                return;
            }

            _dropdownWorlds.Clear();
            var labels = new List<string>();
            var active = WorldRegistry.ActiveWorlds;
            for (int i = 0; i < active.Count; i++)
            {
                _dropdownWorlds.Add(active[i]);
                labels.Add(active[i].DebugName ?? $"World #{i}");
            }

            _worldDropdown.choices = labels;

            if (_dropdownWorlds.Count == 0)
            {
                _worldDropdown.style.display = DisplayStyle.None;
                _emptyState.style.display = DisplayStyle.Flex;
                SetMainPanelsEnabled(false);
                SelectWorld(null);
                return;
            }

            // Hide the dropdown row entirely in the single-world case; it's
            // wasted vertical space when there's nothing to choose between.
            _worldDropdown.style.display =
                _dropdownWorlds.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
            _emptyState.style.display = DisplayStyle.None;
            SetMainPanelsEnabled(true);

            var selectedIndex =
                _selectedWorld == null ? -1 : _dropdownWorlds.IndexOf(_selectedWorld);
            if (selectedIndex < 0)
            {
                var shared = TrecsEditorSelection.ActiveWorld;
                if (shared != null)
                {
                    selectedIndex = _dropdownWorlds.IndexOf(shared);
                }
                if (selectedIndex < 0)
                {
                    selectedIndex = 0;
                }
                SelectWorld(_dropdownWorlds[selectedIndex]);
            }
            _worldDropdown.SetValueWithoutNotify(labels[selectedIndex]);
        }

        void OnWorldDropdownChanged(ChangeEvent<string> evt)
        {
            var index = _worldDropdown.index;
            if (index >= 0 && index < _dropdownWorlds.Count)
            {
                SelectWorld(_dropdownWorlds[index]);
            }
        }

        void SelectWorld(World world)
        {
            if (_selectedWorld == world)
            {
                return;
            }
            ClearAccessor();
            _selectedWorld = world;
            // Backing-file-name is per-session, per-window. The previous
            // world's name doesn't apply to whatever buffer the new world's
            // recorder is on, so reset to "(unsaved)".
            _currentRecordingName = null;
            UpdateRecordingHeader();
            if (world != null)
            {
                TrecsEditorSelection.ActiveWorld = world;
            }
            RefreshTick();
        }

        void ClearAccessor()
        {
            _selectedAccessor = null;
        }

        bool TryGetRunner(out SystemRunner runner)
        {
            runner = null;
            if (_selectedWorld == null || _selectedWorld.IsDisposed)
            {
                return false;
            }
            try
            {
                _selectedAccessor ??= _selectedWorld.CreateAccessor("TrecsTimeTravelWindow");
                runner = _selectedAccessor.GetSystemRunner();
                return runner != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        TrecsGameStateController GetController()
        {
            return _selectedWorld == null
                ? null
                : TrecsGameStateRegistry.GetForWorld(_selectedWorld);
        }

        void RefreshTick()
        {
            if (_selectedWorld == null || _transportPanel == null)
            {
                return;
            }

            if (_selectedWorld.IsDisposed)
            {
                ResetTransportUI();
                UpdateBadge(
                    GameStateMode.Idle,
                    paused: false,
                    byCapacity: false,
                    recorder: null,
                    controllerInstalled: true
                );
                return;
            }

            _selectedAccessor ??= _selectedWorld.CreateAccessor("TrecsTimeTravelWindow");

            var controller = GetController();
            if (controller == null)
            {
                // World is alive but its composition root never wired up a
                // TrecsGameStateController. Surface that loudly via the badge
                // instead of falling through to the LIVE/Idle branch — the two
                // cases are indistinguishable from the user's POV otherwise.
                ResetTransportUI();
                UpdateBadge(
                    GameStateMode.Idle,
                    paused: false,
                    byCapacity: false,
                    recorder: null,
                    controllerInstalled: false
                );
                return;
            }
            controller.PollModeChanged();

            var mode = controller.CurrentMode;
            var paused = controller.IsPaused;
            var recorder = controller.AutoRecorder;
            var byCapacity = recorder?.IsPausedByCapacity ?? false;

            UpdateBadge(mode, paused, byCapacity, recorder, controllerInstalled: true);
            UpdateForkButton(mode);
            SyncTimeScaleFromRunner();
            RefreshScrubber(controller, recorder);
            RefreshBufferInfo(recorder);
            RefreshCapacityBanner(byCapacity, recorder);
            RefreshDesyncBanner(recorder);
            // Set play/pause tooltip + forward-only enable state in one place
            // so order doesn't matter.
            UpdateForwardControlsForCapacity(byCapacity, paused);
        }

        void UpdateForwardControlsForCapacity(bool byCapacity, bool paused)
        {
            // When paused-by-capacity, forward-only controls are disabled so
            // the user can't immediately re-trigger the pause by pressing Play.
            // Backward scrub, jump-to-start and Fork still work so they can
            // inspect the existing buffer and decide how to relieve pressure.
            // Set both states explicitly so we don't rely on call-order with
            // SetTransportEnabled / UpdatePlayPauseButton to recover.
            _playPauseButton.SetEnabled(!byCapacity);
            _stepForwardButton.SetEnabled(!byCapacity);
            var playPauseTooltip = byCapacity
                ? "Recording paused — cap reached. Save / Fork / Reset / raise the cap "
                    + "before resuming."
                : (paused ? "Play (Space)" : "Pause (Space)");
            ApplyTooltip(_playPauseButton, playPauseTooltip);
            // Swap the glyph so the button reflects the action it'd take if
            // pressed: a play arrow when currently paused, two bars when
            // currently running. Falls back gracefully if icons couldn't load.
            if (_playPauseIcon != null)
            {
                var nextIcon = paused ? _iconPlay : _iconPause;
                if (nextIcon != null)
                {
                    _playPauseIcon.image = nextIcon;
                }
            }
            // Loud paused indicator: tint the play/pause button background
            // a vivid green ("press to play") when the simulation is paused.
            // White icon stays default, which contrasts well against the
            // saturated green. (An earlier attempt to tint the icon itself
            // via style.unityBackgroundImageTintColor was a no-op — the
            // Image element renders via its own .image property, not via
            // style.backgroundImage, so that tint channel doesn't apply.)
            // Skip for the capacity-pause state — it already has its own
            // banner + disabled-control treatment.
            _playPauseButton.style.backgroundColor =
                paused && !byCapacity ? new Color(0.15f, 0.6f, 0.25f) : StyleKeyword.Null;
        }

        void ResetTransportUI()
        {
            _bufferInfoLabel.text = string.Empty;
            _capacityBanner.style.display = DisplayStyle.None;
            _desyncBanner.style.display = DisplayStyle.None;
            _forkButton.style.display = DisplayStyle.None;
            _hoverIndicator.style.display = DisplayStyle.None;
            HideHoverTooltip();
            _timelineRuler.Clear();
            _timelineSlider.SetEnabled(false);
            SetTransportEnabled(false);
            SetTimeScaleEnabled(false);
        }

        void UpdateBadge(
            GameStateMode mode,
            bool paused,
            bool byCapacity,
            TrecsAutoRecorder recorder,
            bool controllerInstalled
        )
        {
            if (!controllerInstalled)
            {
                _stateBadge.text = "NO RECORDER";
                _stateBadge.style.backgroundColor = new Color(0.5f, 0.35f, 0.05f);
                _stateBadge.tooltip =
                    "This World has no TrecsGameStateController installed, so "
                    + "recording/scrubbing is unavailable.\n\n"
                    + "Construct TrecsAutoRecorder + "
                    + "TrecsGameStateController and call Initialize() / Dispose() "
                    + "in lockstep with the World.";
                return;
            }
            // Restore the default tooltip on every controller-installed call so
            // a NO RECORDER → installed transition (e.g. user fixes wiring and
            // re-enters Play) doesn't leave the diagnostic tooltip stuck.
            _stateBadge.tooltip = DefaultBadgeTooltip;
            // Desync overrides the mode badge: the user really wants to see
            // this loud and immediate, not buried in a side banner. Mode is
            // implicit from the visible transport controls anyway.
            if (recorder != null && recorder.HasDesynced)
            {
                _stateBadge.text = "✕ DESYNC";
                _stateBadge.style.backgroundColor = new Color(0.7f, 0.15f, 0.15f);
                return;
            }
            // Mode badge stays constant per mode — paused state is signalled
            // separately via the play/pause button colour (see
            // UpdateForwardControlsForCapacity), so the user has a single
            // loud cue rather than a subtle text mutation here.
            string text;
            Color bg;
            switch (mode)
            {
                case GameStateMode.Recording:
                    text = "● REC";
                    bg = byCapacity ? new Color(0.6f, 0.25f, 0.05f) : new Color(0.55f, 0.1f, 0.1f);
                    break;
                case GameStateMode.Playback:
                    text = "▶ PLAY";
                    bg = new Color(0.15f, 0.35f, 0.6f);
                    break;
                default:
                    text = "LIVE";
                    bg = new Color(0.3f, 0.3f, 0.3f);
                    break;
            }
            _stateBadge.text = text;
            _stateBadge.style.backgroundColor = bg;
        }

        void UpdateForkButton(GameStateMode mode)
        {
            var visible = mode == GameStateMode.Playback;
            _forkButton.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible)
            {
                return;
            }
            var recorder = GetController()?.AutoRecorder;
            _forkButton.tooltip =
                recorder?.IsLoadedRecording == true
                    ? "Commit & go live from here. Drops the rest of the loaded recording from "
                        + "memory; the saved file is untouched. (Plain Play walks through "
                        + "the buffer without committing.)"
                    : "Commit & go live: truncate trailing bookmarks at this frame and "
                        + "resume Recording from here. (Plain Play walks through the "
                        + "buffer without truncating.)";
        }

        void RefreshScrubber(TrecsGameStateController controller, TrecsAutoRecorder recorder)
        {
            var recordingActive = recorder != null && recorder.IsRecording;
            SetTransportEnabled(recordingActive);

            if (!recordingActive || recorder.Bookmarks.Count == 0)
            {
                _timelineSlider.SetEnabled(false);
                _hoverIndicator.style.display = DisplayStyle.None;
                _timelineRuler.Clear();
                return;
            }

            _timelineSlider.SetEnabled(true);

            var startFrame = recorder.StartFrame;
            var currentFrame = _selectedAccessor.FixedFrame;
            var maxFrame = Math.Max(currentFrame, recorder.LastBookmarkFrame);

            _suppressControlEvents = true;
            try
            {
                _timelineSlider.lowValue = startFrame;
                _timelineSlider.highValue = maxFrame;
                if (!_isScrubbing)
                {
                    _timelineSlider.SetValueWithoutNotify(currentFrame);
                }
            }
            finally
            {
                _suppressControlEvents = false;
            }

            RefreshTimelineRuler(startFrame, maxFrame);
        }

        // Adaptive ruler under the slider: tick labels at "nice" time
        // intervals (0.1s, 0.5s, 1s, 5s, 30s, 1m, …). Picks the smallest
        // interval that keeps labels at least ~70 px apart so as the buffer
        // grows the labels never overlap. Labels are positioned in percent
        // so they reflow with track resizes.
        void RefreshTimelineRuler(int startFrame, int maxFrame)
        {
            _timelineRuler.Clear();
            if (!TryGetFixedDeltaTime(out var dt))
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
            _timelineRuler.style.marginLeft = trackLeft;
            _timelineRuler.style.width = trackWidth;

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
                _timelineRuler.Add(tickLine);

                var label = new Label(FormatRulerLabel(t));
                label.style.position = Position.Absolute;
                label.style.left = new Length(fraction * 100f, LengthUnit.Percent);
                label.style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
                label.style.top = 3;
                label.style.fontSize = 9;
                label.style.color = new Color(1f, 1f, 1f, 0.55f);
                label.pickingMode = PickingMode.Ignore;
                _timelineRuler.Add(label);
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

        bool TryGetFixedDeltaTime(out float dt)
        {
            if (TryGetRunner(out var runner))
            {
                dt = runner.FixedDeltaTime;
                return dt > 0f;
            }
            dt = 0f;
            return false;
        }

        void RefreshBufferInfo(TrecsAutoRecorder recorder)
        {
            if (recorder == null)
            {
                _bufferInfoLabel.text = string.Empty;
                return;
            }
            if (!recorder.IsRecording)
            {
                _bufferInfoLabel.text = "idle";
                return;
            }
            if (recorder.Bookmarks.Count == 0)
            {
                _bufferInfoLabel.text = "awaiting first bookmark";
                return;
            }
            var span = recorder.LastBookmarkFrame - recorder.StartFrame;
            var seconds = TryGetFixedDeltaTime(out var dt) ? span * dt : 0f;
            _bufferInfoLabel.text =
                $"{recorder.Bookmarks.Count} bookmarks · "
                + $"{span} frames ({FormatDuration(seconds)}) · "
                + $"{FormatBytes(recorder.TotalBytes)}";
        }

        static string FormatDuration(float seconds)
        {
            if (seconds <= 0f)
            {
                return "0s";
            }
            if (seconds < 60f)
            {
                return $"{seconds:F1}s";
            }
            var m = (int)(seconds / 60);
            var s = seconds - m * 60;
            return $"{m}m{s:F0}s";
        }

        void RefreshCapacityBanner(bool byCapacity, TrecsAutoRecorder recorder)
        {
            if (byCapacity && recorder != null)
            {
                _capacityBanner.text =
                    "Recording paused — capacity reached "
                    + $"({recorder.Bookmarks.Count} bookmarks, {FormatBytes(recorder.TotalBytes)}). "
                    + "Save, fork, reset, or raise the cap before resuming.";
                _capacityBanner.style.display = DisplayStyle.Flex;
            }
            else
            {
                _capacityBanner.style.display = DisplayStyle.None;
            }
        }

        void RefreshDesyncBanner(TrecsAutoRecorder recorder)
        {
            if (recorder != null && recorder.HasDesynced)
            {
                _desyncBanner.text =
                    $"Desync at frame {recorder.DesyncedFrame.Value} — re-running the simulation "
                    + "from an earlier bookmark produced different state than originally "
                    + "captured. Check the editor log for the checksum mismatch.";
                _desyncBanner.style.display = DisplayStyle.Flex;
            }
            else
            {
                _desyncBanner.style.display = DisplayStyle.None;
            }
        }

        void SetTransportEnabled(bool enabled)
        {
            _jumpStartButton.SetEnabled(enabled);
            _stepBackButton.SetEnabled(enabled);
            _playPauseButton.SetEnabled(enabled);
            _stepForwardButton.SetEnabled(enabled);
            _jumpEndButton.SetEnabled(enabled);
        }

        // When no world is active (typically: not in play mode), keep the panels
        // visible but grayed out so the window's affordances are still
        // discoverable instead of replaced by a single empty-state label.
        void SetMainPanelsEnabled(bool enabled)
        {
            _recordingHeaderRow.SetEnabled(enabled);
            _transportPanel.SetEnabled(enabled);
            _recordingHeaderRow.style.opacity = enabled ? 1f : 0.4f;
            _transportPanel.style.opacity = enabled ? 1f : 0.4f;
            // Buffer-info sits on rootVisualElement (above the scroll) and
            // shows live stats — meaningless without a world, so hide it
            // entirely rather than gray it.
            _bufferInfoLabel.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void SetTimeScaleEnabled(bool enabled)
        {
            _speedButton.SetEnabled(enabled);
        }

        void SyncTimeScaleFromRunner()
        {
            if (!TryGetRunner(out var runner))
            {
                SetTimeScaleEnabled(false);
                return;
            }
            SetTimeScaleEnabled(true);

            _speedButton.text = FormatSpeedLabel(runner.TimeScale);
            // Tint the button amber whenever the runner isn't at real-time —
            // easy to forget you've set 0.1× or 2× and end up confused about
            // why the world feels slow/fast.
            HighlightSpeedButton(!Mathf.Approximately(runner.TimeScale, 1f));
        }

        void HighlightSpeedButton(bool nonDefault)
        {
            var amber = new Color(0.95f, 0.7f, 0.1f);
            _speedButton.style.color = nonDefault ? amber : (StyleColor)StyleKeyword.Null;
            _speedButton.style.unityFontStyleAndWeight = nonDefault
                ? FontStyle.Bold
                : (StyleEnum<FontStyle>)StyleKeyword.Null;
        }

        // Render a speed multiplier with a tidy "×" suffix. Whole numbers
        // (1, 2) get no decimal; fractional values keep the leading zero
        // dropped so "0.5" reads as "0.5×" not ".5×".
        static string FormatSpeedLabel(float speed)
        {
            if (Mathf.Approximately(speed, Mathf.Round(speed)))
            {
                return $"{Mathf.RoundToInt(speed)}×";
            }
            return $"{speed:0.##}×";
        }

        // ---- Transport callbacks ----

        void OnPlayPauseClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                return;
            }
            controller.SetPaused(!controller.IsPaused);
        }

        void OnStepForwardClicked()
        {
            var controller = GetController();
            controller?.StepFrame();
        }

        void OnStepBackClicked()
        {
            var controller = GetController();
            if (controller == null || !TryGetRunner(out var runner))
            {
                return;
            }
            runner.FixedIsPaused = true;
            controller.JumpToFrame(_selectedAccessor.FixedFrame - 1);
        }

        void OnJumpStartClicked()
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (controller == null || recorder == null || recorder.Bookmarks.Count == 0)
            {
                return;
            }
            controller.JumpToFrame(recorder.StartFrame);
        }

        void OnJumpEndClicked()
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (controller == null || recorder == null || recorder.Bookmarks.Count == 0)
            {
                return;
            }
            controller.JumpToFrame(recorder.LastBookmarkFrame);
        }

        void OnForkClicked()
        {
            var controller = GetController();
            controller?.ForkAtCurrentFrame();
        }

        void OnSettingsClicked()
        {
            var recorder = GetController()?.AutoRecorder;
            if (recorder == null)
            {
                SetRecordingStatus("No active recorder to configure.");
                return;
            }
            TrecsRecorderSettingsWindow.Show(recorder, this);
        }

        void OnTrimButtonClicked()
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (recorder == null || !recorder.IsRecording || recorder.Bookmarks.Count == 0)
            {
                SetRecordingStatus("Nothing to trim — no recording.");
                return;
            }
            if (_selectedAccessor == null)
            {
                SetRecordingStatus("Nothing to trim — no active world.");
                return;
            }

            var current = _selectedAccessor.FixedFrame;
            var strictlyBefore = 0;
            var hasExactBookmark = false;
            var after = 0;
            for (var i = 0; i < recorder.Bookmarks.Count; i++)
            {
                var f = recorder.Bookmarks[i].Frame;
                if (f < current)
                    strictlyBefore++;
                else if (f > current)
                    after++;
                else
                    hasExactBookmark = true;
            }
            // TrimRecordingBefore preserves the closest bookmark at-or-before
            // `current` so JumpToFrame(current) still resolves. If `current` is
            // itself a bookmark frame, that's the anchor and all strictly-prior
            // bookmarks drop. Otherwise the anchor is the last strictly-prior
            // bookmark, so one fewer drops.
            var before = hasExactBookmark ? strictlyBefore : Math.Max(0, strictlyBefore - 1);

            var menu = new GenericMenu();
            var beforeLabel =
                $"Trim ◀ before frame {current} (drops {before} bookmark{(before == 1 ? "" : "s")})";
            var afterLabel =
                $"Trim ▶ after frame {current} (drops {after} bookmark{(after == 1 ? "" : "s")})";
            if (before > 0)
            {
                menu.AddItem(
                    new GUIContent(beforeLabel),
                    false,
                    () => DoTrim(trimBefore: true, current)
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(beforeLabel));
            }
            if (after > 0)
            {
                menu.AddItem(
                    new GUIContent(afterLabel),
                    false,
                    () => DoTrim(trimBefore: false, current)
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(afterLabel));
            }
            menu.DropDown(_trimButton.worldBound);
        }

        void DoTrim(bool trimBefore, int frame)
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (recorder == null)
                return;
            var direction = trimBefore ? "before" : "after";
            if (
                !EditorUtility.DisplayDialog(
                    "Trim recording?",
                    $"Drop bookmarks {direction} frame {frame} from the in-memory buffer? "
                        + "Saved files on disk are not affected.",
                    "Trim",
                    "Cancel"
                )
            )
            {
                return;
            }
            var dropped = trimBefore
                ? recorder.TrimRecordingBefore(frame)
                : recorder.TrimRecordingAfter(frame);
            if (dropped == 0)
            {
                SetRecordingStatus($"Nothing to trim {direction} frame {frame}.");
                return;
            }
            SetRecordingStatus(
                $"Trimmed {dropped} bookmark{(dropped == 1 ? "" : "s")} {direction} frame {frame}."
            );
            RefreshTick();
        }

        // ---- Recording header callbacks ----

        // Status messages are transient feedback for the last save/load/delete/new
        // action. The recording header itself shows persistent state (current
        // filename + dirty marker), so the status line only needs to flash briefly.
        void SetRecordingStatus(string text)
        {
            _recordingStatusClearTask?.Pause();
            _recordingStatusLabel.text = text;
            if (string.IsNullOrEmpty(text))
            {
                _recordingStatusClearTask = null;
                return;
            }
            _recordingStatusClearTask = _recordingStatusLabel
                .schedule.Execute(() => _recordingStatusLabel.text = "")
                .StartingIn(RecordingStatusClearDelayMs);
        }

        void OnNewClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetRecordingStatus("No active world.");
                return;
            }
            var recorder = controller.AutoRecorder;
            // Skip the confirm if the buffer is trivially small (just hit
            // Play, no data to lose) — same threshold used by the old
            // "Reset auto-recording" advanced button.
            var hasRealData = recorder != null && recorder.Bookmarks.Count > 4;
            if (hasRealData)
            {
                var diskNote = string.IsNullOrEmpty(_currentRecordingName)
                    ? " The unsaved buffer will be lost."
                    : $" Saved file '{_currentRecordingName}' on disk is not affected.";
                if (
                    !EditorUtility.DisplayDialog(
                        "Start a new recording?",
                        $"Discard {recorder.Bookmarks.Count} in-memory bookmarks "
                            + $"({FormatBytes(recorder.TotalBytes)})?"
                            + diskNote,
                        "Discard & start fresh",
                        "Cancel"
                    )
                )
                {
                    return;
                }
            }
            controller.ResetAutoRecording();
            _currentRecordingName = null;
            UpdateRecordingHeader();
            SetRecordingStatus("Started a new recording.");
            RefreshTick();
        }

        void OnSaveClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetRecordingStatus("No active world.");
                return;
            }
            var name = _currentRecordingName;
            if (string.IsNullOrEmpty(name))
            {
                name = TrecsTextPromptWindow.Prompt(
                    "Save recording",
                    "Enter recording name:",
                    SuggestRecordingName(),
                    anchor: this
                );
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }
                name = name.Trim();
            }
            DoSaveRecording(controller, name);
        }

        void OnSaveAsClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetRecordingStatus("No active world.");
                return;
            }
            var suggested = string.IsNullOrEmpty(_currentRecordingName)
                ? SuggestRecordingName()
                : _currentRecordingName;
            var name = TrecsTextPromptWindow.Prompt(
                "Save recording as",
                "Save as:",
                suggested,
                anchor: this
            );
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            DoSaveRecording(controller, name.Trim());
        }

        void DoSaveRecording(TrecsGameStateController controller, string name)
        {
            try
            {
                if (controller.SaveNamedRecording(name))
                {
                    _currentRecordingName = name;
                    UpdateRecordingHeader();
                    SetRecordingStatus($"Saved '{name}'.");
                }
                else
                {
                    SetRecordingStatus("Save failed (no bookmarks to save?).");
                }
            }
            catch (Exception e)
            {
                SetRecordingStatus($"Save failed: {e.Message}");
            }
        }

        void OnLoadClicked()
        {
            // GenericMenu drops down right under the Load button — matches
            // editor convention for "pick from a list" affordances and avoids
            // a separate browser window.
            var menu = new GenericMenu();
            var names = TrecsGameStateController.GetSavedRecordingNames();
            if (names.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("(no saved recordings)"));
            }
            else
            {
                foreach (var n in names)
                {
                    var captured = n;
                    menu.AddItem(
                        new GUIContent(captured),
                        captured == _currentRecordingName,
                        () => DoLoadRecording(captured)
                    );
                }
            }
            menu.DropDown(_loadButton.worldBound);
        }

        void DoLoadRecording(string name)
        {
            var controller = GetController();
            if (controller == null)
            {
                SetRecordingStatus("No active world to load into.");
                return;
            }
            try
            {
                if (controller.LoadNamedRecording(name))
                {
                    _currentRecordingName = name;
                    UpdateRecordingHeader();
                    SetRecordingStatus($"Loaded '{name}'.");
                    RefreshTick();
                }
                else
                {
                    SetRecordingStatus("Load failed.");
                }
            }
            catch (Exception e)
            {
                SetRecordingStatus($"Load failed: {e.Message}");
            }
        }

        void OnDeleteClicked()
        {
            var name = _currentRecordingName;
            if (string.IsNullOrEmpty(name))
            {
                SetRecordingStatus("No saved file to delete (current recording is unsaved).");
                return;
            }
            if (
                !EditorUtility.DisplayDialog(
                    "Delete recording?",
                    $"Delete saved recording '{name}'? This removes the file from disk; "
                        + "the in-memory buffer is unchanged.",
                    "Delete",
                    "Cancel"
                )
            )
            {
                return;
            }
            var controller = GetController();
            try
            {
                if (controller != null)
                {
                    controller.DeleteNamedRecording(name);
                }
                else
                {
                    DeleteFileIfExists(TrecsGameStateController.GetRecordingPath(name));
                }
                _currentRecordingName = null;
                UpdateRecordingHeader();
                SetRecordingStatus($"Deleted '{name}' from disk.");
            }
            catch (Exception e)
            {
                SetRecordingStatus($"Delete failed: {e.Message}");
            }
        }

        static string SuggestRecordingName() => $"recording-{DateTime.Now:yyyyMMdd-HHmmss}";

        void UpdateRecordingHeader()
        {
            if (_recordingNameLabel == null)
            {
                return;
            }
            var saved = !string.IsNullOrEmpty(_currentRecordingName);
            var displayName = saved ? _currentRecordingName : UnsavedRecordingDisplayName;
            _recordingNameLabel.text = displayName;
            _recordingNameLabel.style.unityFontStyleAndWeight = saved
                ? FontStyle.Bold
                : FontStyle.Italic;
            _recordingNameLabel.style.opacity = saved ? 1f : 0.7f;
            // Highlight the saved name so it reads as the header's identity
            // without needing a "Recording:" prefix label. Unsaved falls back
            // to the default text color (dimmed via opacity above).
            _recordingNameLabel.style.color = saved
                ? new StyleColor(new Color(0.55f, 0.85f, 1f))
                : new StyleColor(StyleKeyword.Null);
            // Surface the full name via the hover tooltip so the user can
            // still read it when the label gets ellipsised by the row.
            ApplyTooltip(
                _recordingNameLabel,
                saved
                    ? $"Recording: {displayName}\n(file backing the in-memory recording)"
                    : "Recording is unsaved — Save to persist to disk."
            );
            _deleteButton?.SetEnabled(saved);
        }

        // ---- Scrubber callbacks ----

        void OnTimelinePointerDown(PointerDownEvent evt)
        {
            // showInputField=true puts an IntegerField next to the slider track.
            // Clicking inside that field would otherwise enter scrub mode and
            // throttle-jump on every keystroke as the user types — bail out.
            if (evt.target is VisualElement target && IsInsideIntegerField(target))
            {
                return;
            }
            _isScrubbing = true;
            _pendingScrubFrame = _timelineSlider.value;
            _lastCommittedScrubFrame = _selectedAccessor?.FixedFrame ?? _pendingScrubFrame;
            _lastScrubCommitTime = EditorApplication.timeSinceStartup;
            _hoverFrame = null;
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

        void OnTimelinePointerMove(PointerMoveEvent evt)
        {
            if (_isScrubbing || !_timelineSlider.enabledInHierarchy)
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
                    Mathf.Lerp(_timelineSlider.lowValue, _timelineSlider.highValue, fraction)
                );
                _hoverFrame = frame;
                _hoverIndicator.style.left = Mathf.Round(localX);
                _hoverIndicator.style.display = DisplayStyle.Flex;
                ShowHoverTooltip(localX, frame);
            }
            else
            {
                _hoverFrame = null;
                _hoverIndicator.style.display = DisplayStyle.None;
                HideHoverTooltip();
            }
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
            var dragContainer = _timelineSlider.Q(className: "unity-base-slider__drag-container");
            var dragger = _timelineSlider.Q(className: "unity-base-slider__dragger");
            if (
                dragContainer != null
                && dragContainer.layout.width > 0
                && dragger != null
                && dragger.layout.width > 0
            )
            {
                var dcWorld = dragContainer.worldBound;
                var sliderWorld = _timelineSlider.worldBound;
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
                var sliderWorld = _timelineSlider.worldBound;
                left = dcWorld.x - sliderWorld.x;
                width = dcWorld.width;
                return true;
            }
            // Tracker fallback for early-layout cases.
            var tracker = _timelineSlider.Q(className: "unity-base-slider__tracker");
            if (tracker != null && tracker.layout.width > 0)
            {
                var trackerWorld = tracker.worldBound;
                var sliderWorld = _timelineSlider.worldBound;
                left = trackerWorld.x - sliderWorld.x;
                width = trackerWorld.width;
                return true;
            }
            // Final fallback: whole-slider span if nothing's laid out yet.
            var w = _timelineSlider.resolvedStyle.width;
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

        void OnTimelinePointerLeave(PointerLeaveEvent evt)
        {
            _hoverFrame = null;
            _hoverIndicator.style.display = DisplayStyle.None;
            HideHoverTooltip();
        }

        // Floating hover tooltip pinned above the vertical hover indicator
        // line. Shows the cursor's frame and a signed time offset relative
        // to the current frame ("412 (+12s)") so users can pick a scrub
        // target. Anchored to the indicator (not the cursor) so the tooltip
        // stays in line with the visual marker as the user moves.
        void ShowHoverTooltip(float trackLocalX, int hoverFrame)
        {
            if (_isScrubbing || _selectedAccessor == null)
            {
                HideHoverTooltip();
                return;
            }
            EnsureHoverTooltipLabel();
            var current = _selectedAccessor.FixedFrame;
            _hoverTooltipLabel.text =
                $"{hoverFrame} ({FormatRelativeFrameDelta(hoverFrame - current)})";
            _hoverTooltipLabel.style.display = DisplayStyle.Flex;
            _hoverTooltipLabel.BringToFront();
            PositionHoverTooltip(trackLocalX);
            // resolvedStyle width/height aren't valid on the very first
            // show (no layout pass yet); reposition once layout settles so
            // the centred + clamped placement is correct from frame two
            // onward. Subsequent shows already have valid resolvedStyle so
            // PositionHoverTooltip above places it correctly the same frame.
            _hoverTooltipLabel
                .schedule.Execute(() =>
                {
                    if (_hoverTooltipLabel.style.display == DisplayStyle.None)
                        return;
                    PositionHoverTooltip(trackLocalX);
                })
                .ExecuteLater(0);
        }

        void PositionHoverTooltip(float trackLocalX)
        {
            var rootBound = rootVisualElement.worldBound;
            var sliderBound = _timelineSlider.worldBound;
            var w = _hoverTooltipLabel.resolvedStyle.width;
            var h = _hoverTooltipLabel.resolvedStyle.height;
            // Indicator's root-local x = slider's root-local x + slider-local
            // x of the indicator (which is trackLocalX). Centre the tooltip
            // horizontally on it; clamp inside the window so the tooltip
            // doesn't slide off near either end of the slider.
            var indicatorRootX = sliderBound.x - rootBound.x + trackLocalX;
            var x = Mathf.Clamp(
                indicatorRootX - w / 2f,
                4f,
                Mathf.Max(4f, rootBound.width - w - 4f)
            );
            // Sit just above the slider's top edge.
            var y = sliderBound.y - rootBound.y - h - 4f;
            _hoverTooltipLabel.style.left = x;
            _hoverTooltipLabel.style.top = y;
        }

        void HideHoverTooltip()
        {
            if (_hoverTooltipLabel != null)
            {
                _hoverTooltipLabel.style.display = DisplayStyle.None;
            }
        }

        void EnsureHoverTooltipLabel()
        {
            if (_hoverTooltipLabel != null)
                return;
            _hoverTooltipLabel = new Label();
            _hoverTooltipLabel.style.position = Position.Absolute;
            _hoverTooltipLabel.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 0.97f);
            _hoverTooltipLabel.style.color = Color.white;
            _hoverTooltipLabel.style.fontSize = 11;
            _hoverTooltipLabel.style.paddingLeft = 6;
            _hoverTooltipLabel.style.paddingRight = 6;
            _hoverTooltipLabel.style.paddingTop = 2;
            _hoverTooltipLabel.style.paddingBottom = 2;
            _hoverTooltipLabel.style.borderTopLeftRadius = 3;
            _hoverTooltipLabel.style.borderTopRightRadius = 3;
            _hoverTooltipLabel.style.borderBottomLeftRadius = 3;
            _hoverTooltipLabel.style.borderBottomRightRadius = 3;
            _hoverTooltipLabel.pickingMode = PickingMode.Ignore;
            _hoverTooltipLabel.style.display = DisplayStyle.None;
            rootVisualElement.Add(_hoverTooltipLabel);
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
            if (!TryGetFixedDeltaTime(out var dt))
            {
                return deltaFrames > 0 ? $"+{deltaFrames} frames" : $"{deltaFrames} frames";
            }
            var deltaSeconds = deltaFrames * dt;
            var sign = deltaSeconds > 0 ? "+" : "-";
            return sign + FormatRulerLabel(Mathf.Abs(deltaSeconds));
        }

        void OnTimelinePointerUp(PointerUpEvent evt)
        {
            FinalizeScrub();
        }

        void OnTimelinePointerCaptureOut(PointerCaptureOutEvent evt)
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
                GetController()?.JumpToFrame(target);
                _lastCommittedScrubFrame = target;
            }
        }

        void OnTimelineValueChanged(ChangeEvent<int> evt)
        {
            if (_suppressControlEvents)
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
                GetController()?.JumpToFrame(evt.newValue);
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
            GetController()?.JumpToFrame(_pendingScrubFrame);
            _lastCommittedScrubFrame = _pendingScrubFrame;
            _lastScrubCommitTime = now;
        }

        // ---- Speed dropdown ----

        void OnSpeedButtonClicked()
        {
            var runner = TryGetRunner(out var r) ? r : null;
            var current = runner?.TimeScale ?? 1f;
            var menu = new GenericMenu();
            foreach (var preset in SpeedPresets)
            {
                var captured = preset;
                var isCurrent = Mathf.Approximately(current, captured);
                menu.AddItem(
                    new GUIContent(FormatSpeedLabel(captured)),
                    isCurrent,
                    () => SetTimeScale(captured)
                );
            }
            menu.DropDown(_speedButton.worldBound);
        }

        void SetTimeScale(float speed)
        {
            if (TryGetRunner(out var runner))
            {
                runner.TimeScale = speed;
                _speedButton.text = FormatSpeedLabel(speed);
                HighlightSpeedButton(!Mathf.Approximately(speed, 1f));
            }
        }

        // ---- Misc helpers ----

        static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        static bool IsInsideTextField(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
            {
                if (e is TextField)
                {
                    return true;
                }
            }
            return false;
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }
            if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
